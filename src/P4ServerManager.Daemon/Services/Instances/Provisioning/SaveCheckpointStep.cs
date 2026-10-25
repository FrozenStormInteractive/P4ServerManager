using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Dumps the database of an imported server to a compressed checkpoint in the folder of the instance, before anything
/// writes to it. <c>-jd</c> leaves the journal as it is, unlike <c>-jc</c>.
/// </summary>
public sealed class SaveCheckpointStep(IP4ServerBuildCache buildCache, IProcessRunner processRunner, DaemonPaths paths)
    : IOperationStep<ProvisioningContext>
{
    /// <summary>
    /// The dump reads every table, and the upgrade may rewrite them: both take as long as the database is big.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromHours(2);

    public string Key => "save-checkpoint";

    public string Title => "Save a checkpoint";

    public async Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        string checkpointPath = paths.GetImportCheckpointPath(instance.Id);
        await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(instance.Build!),
                [.. P4ServerLauncher.GetRootArguments(instance), "-z", "-jd", checkpointPath],
                Timeout: Timeout),
            cancellationToken);
        return checkpointPath;
    }
}
