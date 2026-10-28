using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Creates the super user given at creation, and makes it the only super user of the protections table. The table also
/// hides the spec depot from the other users.
/// </summary>
public sealed class CreateSuperUserStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    ILogger<CreateSuperUserStep> logger) : IOperationStep<ProvisioningContext>
{
    /// <summary>
    /// Added after the <c>write user</c> line of the default table.
    /// </summary>
    private const string HideSpecDepotLine = "\tlist user * * -//spec/...";

    public string Key => "create-super-user";

    public string Title => "Create the super user";

    public async Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4AdminSession session = await P4AdminSession.OpenAsync(
            buildCache,
            processRunner,
            paths,
            logger,
            context,
            cancellationToken);
        string name = context.SuperUser!.Name;

        bool specSaved = await session.SetPasswordAsync(cancellationToken);
        return await session.RunLoggedInAsync(async () =>
        {
            if (!specSaved)
            {
                await session.RunAsync(["user", "-f", "-i"], session.GetUserSpec(), cancellationToken);
            }

            // Saving the protections table for the first time makes the current user the only super user.
            ProcessResult protections = await session.RunAsync(["protect", "-o"], cancellationToken: cancellationToken);
            await session.RunAsync(
                ["protect", "-i"],
                AddHideSpecDepotLine(protections.StandardOutput),
                cancellationToken);
            return name;
        }, cancellationToken);
    }

    /// <summary>
    /// Adds <see cref="HideSpecDepotLine"/> after the <c>write user</c> line of a protections table.
    /// </summary>
    internal static string AddHideSpecDepotLine(string protections)
    {
        List<string> lines = protections.ReplaceLineEndings("\n").Split('\n').ToList();
        int writeLine = lines.FindIndex(line => line.TrimStart().StartsWith("write user ", StringComparison.Ordinal));
        if (writeLine < 0)
        {
            throw new InstanceOperationException("The protections table has no 'write user' line.");
        }
        lines.Insert(writeLine + 1, HideSpecDepotLine);
        return string.Join('\n', lines);
    }
}
