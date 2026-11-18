using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Turns the restored database into the one of the new instance, before its files are placed: reads where the server
/// that was backed up kept its files, then gives it the name of the instance as server ID and the folders of the
/// standard layout, set for that server ID so that they win over the values of the point.
/// </summary>
public sealed class ConfigureRestoredServerStep(
    ServerFileLocator locator,
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner) : IOperationStep<NewInstanceRestoreContext>
{
    public string Key => "configure-restored-server";

    public string Title => "Configure the restored server";

    public async Task<string?> RunAsync(
        NewInstanceRestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            context.OriginalDescription = await locator.ReadRestoredDescriptionAsync(
                context.Instance,
                context.PointServerId,
                cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new InstanceOperationException(
                $"The layout of the restored server cannot be read for sure. {exception.Message}");
        }

        string p4dPath = buildCache.GetP4dPath(context.Instance.Build
            ?? throw new InvalidOperationException("The instance has no p4d build yet."));
        // The journal of the point may name a folder of another machine: nothing is journaled until the layout is set.
        IReadOnlyList<string> root = [.. P4ServerLauncher.GetRootArguments(context.Instance), "-J", "off"];
        TimeSpan timeout = ProcessRunnerExtensions.CommandTimeout;
        string serverId = context.NewServerId;

        string serverIdPath = Path.Combine(context.Instance.RootPath, ServerLayout.ServerIdFile);
        if (File.Exists(serverIdPath))
        {
            File.Delete(serverIdPath);
        }
        await processRunner.RunCheckedAsync(
            new ProcessCommand(p4dPath, [.. root, "-xD", serverId], Timeout: timeout),
            cancellationToken);

        // The folders of the layout are set for any server, as a new instance has them: a value the point kept for its
        // own server ID, or for the new one, would win, so it is removed. Its other own values follow it to the new
        // server ID: the server spec then lists the same configuration under both IDs, which p4d asks to rename it.
        // Relative to the root. One argument each: p4d reads "-c" followed by the command.
        Dictionary<string, string> layout = new(StringComparer.Ordinal)
        {
            [ServerConfigurables.Journal] = $"../{ServerLayout.JournalsFolder}/journal",
            [ServerConfigurables.Log] = $"../{ServerLayout.LogsFolder}/{ServerLayout.LogFile}",
            [ServerConfigurables.JournalPrefix] = $"../{ServerLayout.JournalsFolder}/{serverId}",
            [ServerConfigurables.DepotRoot] = $"../{ServerLayout.ArchivesFolder}",
        };
        HashSet<string> ownerIds = [serverId];
        if (context.OldServerId is { } oldId)
        {
            ownerIds.Add(oldId);
            ProcessResult shown = await processRunner.RunCheckedAsync(
                new ProcessCommand(p4dPath, [.. root, "-cshow"], Timeout: timeout),
                cancellationToken);
            if (oldId != serverId)
            {
                foreach ((string name, string value) in ConfigShowParser.ParseOwn(shown.StandardOutput, oldId))
                {
                    if (!layout.ContainsKey(name))
                    {
                        await processRunner.RunCheckedAsync(
                            new ProcessCommand(p4dPath, [.. root, $"-cset {serverId}#{name}={value}"], Timeout: timeout),
                            cancellationToken);
                    }
                }
            }
        }

        foreach ((string name, string value) in layout)
        {
            foreach (string ownerId in ownerIds)
            {
                // Fails when there is no such value, which is fine.
                await processRunner.RunAsync(
                    new ProcessCommand(p4dPath, [.. root, $"-cunset {ownerId}#{name}"], Timeout: timeout),
                    cancellationToken);
            }
            await processRunner.RunCheckedAsync(
                new ProcessCommand(p4dPath, [.. root, $"-cset {name}={value}"], Timeout: timeout),
                cancellationToken);
        }

        return context.OldServerId is null || context.OldServerId == serverId
            ? $"Server ID {serverId}"
            : $"Server ID {serverId}, was {context.OldServerId}";
    }
}
