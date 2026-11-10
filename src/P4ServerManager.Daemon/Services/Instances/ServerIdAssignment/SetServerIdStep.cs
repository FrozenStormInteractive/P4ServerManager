using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;

/// <summary>
/// Sets the server ID of the stopped p4d with <c>p4d -xD</c>. p4d itself tells the server ID before and after: the old
/// one is kept in the context for the rename of the spec, and the new one is checked, so that the change does not rest
/// on the exit code alone.
/// </summary>
/// <remarks>
/// <c>p4d -xD</c> refuses to replace a server ID. As Perforce documents it, the <c>server.id</c> file of the root is
/// removed first; it is written back if p4d then fails to set the new server ID.
/// </remarks>
public sealed class SetServerIdStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    InstanceNotifier notifier) : IOperationStep<ServerIdAssignmentContext>
{
    public string Key => "set-server-id";

    public string Title => "Set the server ID";

    public async Task<string?> RunAsync(
        ServerIdAssignmentContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        string p4dPath = buildCache.GetP4dPath(context.Build);
        string[] root = [.. P4ServerLauncher.GetRootArguments(instance)];

        context.OldServerId = await ReadServerIdAsync(p4dPath, root, cancellationToken);

        // Not canceled once begun: the server ID and the spec that bears it would no longer match.
        string serverIdPath = ServerLayout.GetServerIdPath(instance.RootPath);
        string? previousFile = File.Exists(serverIdPath)
            ? await File.ReadAllTextAsync(serverIdPath, CancellationToken.None)
            : null;
        FileAttributes previousAttributes = previousFile is null ? default : File.GetAttributes(serverIdPath);
        try
        {
            if (previousFile is not null)
            {
                DeleteServerIdFile(serverIdPath);
            }

            await processRunner.RunCheckedAsync(
                new ProcessCommand(
                    p4dPath,
                    [.. root, "-xD", context.NewServerId],
                    Timeout: ProcessRunnerExtensions.CommandTimeout),
                CancellationToken.None);

            string? serverId = await ReadServerIdAsync(p4dPath, root, CancellationToken.None);
            if (serverId != context.NewServerId)
            {
                throw new InstanceOperationException(
                    $"p4d did not set the server ID to {context.NewServerId}: it reports {serverId ?? "none"}.");
            }
        }
        catch when (previousFile is not null)
        {
            if (File.Exists(serverIdPath))
            {
                DeleteServerIdFile(serverIdPath);
            }
            await File.WriteAllTextAsync(serverIdPath, previousFile, CancellationToken.None);
            File.SetAttributes(serverIdPath, previousAttributes);
            throw;
        }

        // The server ID shown by the daemon is read from the root.
        await notifier.InstanceChangedAsync(instance);
        return context.OldServerId is null
            ? context.NewServerId
            : $"{context.OldServerId} to {context.NewServerId}";
    }

    /// <summary>
    /// Deletes the <c>server.id</c> file. p4d writes it read-only, which forbids its deletion on Windows.
    /// </summary>
    private static void DeleteServerIdFile(string path)
    {
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    /// <summary>
    /// Asks p4d for its server ID, with <c>p4d -xD</c> and no server ID to set.
    /// </summary>
    /// <returns>The server ID, or null when the server has none.</returns>
    private async Task<string?> ReadServerIdAsync(
        string p4dPath,
        string[] root,
        CancellationToken cancellationToken)
    {
        ProcessCommand command = new(p4dPath, [.. root, "-xD"], Timeout: ProcessRunnerExtensions.CommandTimeout);
        ProcessResult result = await processRunner.RunAsync(command, cancellationToken);
        string output = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        if (!result.Succeeded && !ServerIdOutputParser.IsServerIdMessage(output))
        {
            throw new InstanceOperationException($"'p4d -xD' failed: {result.ErrorText}");
        }

        try
        {
            return ServerIdOutputParser.Parse(output);
        }
        catch (FormatException exception)
        {
            throw new InstanceOperationException(exception.Message);
        }
    }
}
