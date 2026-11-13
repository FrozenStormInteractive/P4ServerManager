using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Points the depots whose files the restore put under <c>server.depot.root</c> to their new folder, as the super user
/// of the restored server. Without such depots, nothing is done.
/// </summary>
public sealed class RelocateDepotsStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    ILogger<RelocateDepotsStep> logger) : IOperationStep<NewInstanceRestoreContext>
{
    public string Key => "relocate-depots";

    public string Title => "Relocate the depots";

    public async Task<string?> RunAsync(
        NewInstanceRestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DepotRelocation> relocations = context.Plan?.Relocations ?? [];
        if (relocations.Count == 0)
        {
            return "No depot to relocate";
        }

        SuperUserCredentials superUser = context.SuperUser
            ?? throw new InstanceOperationException("Depots must be relocated, and no super user is given.");
        P4AdminSession session = await P4AdminSession.OpenAsync(
            buildCache,
            processRunner,
            paths,
            logger,
            context.Instance,
            superUser,
            cancellationToken);
        await session.RunLoggedInAsync(
            async () =>
            {
                foreach (DepotRelocation relocation in relocations)
                {
                    progress.Report(new OperationStepProgress(relocation.Name, null));
                    string spec = (await session.RunAsync(
                            ["--field", $"Map={relocation.Map}", "depot", "-o", relocation.Name],
                            cancellationToken: cancellationToken))
                        .StandardOutput;
                    await session.RunAsync(["depot", "-i"], spec, cancellationToken);
                }
                return true;
            },
            cancellationToken);
        return string.Join(", ", relocations.Select(r => $"{r.Name} to {r.Map}"));
    }
}
