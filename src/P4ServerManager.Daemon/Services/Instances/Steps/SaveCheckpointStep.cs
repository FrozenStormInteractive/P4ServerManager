using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Steps;

/// <summary>
/// Dumps the database to a compressed checkpoint, with the build the instance runs, before anything writes to it.
/// <c>-jd</c> leaves the journal as it is, unlike <c>-jc</c>.
/// </summary>
public sealed class SaveCheckpointStep(IP4ServerBuildCache buildCache, IProcessRunner processRunner)
    : IOperationStep<ICheckpointContext>
{
    /// <summary>
    /// The dump reads every table, and an upgrade or a conversion may rewrite them: both take as long as the database
    /// is big.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromHours(2);

    public string Key => "save-checkpoint";

    public string Title => "Save a checkpoint";

    public async Task<string?> RunAsync(
        ICheckpointContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        string checkpointPath = context.CheckpointPath;
        Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);
        await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(instance.Build!),
                [.. P4ServerLauncher.GetRootArguments(instance), "-z", "-jd", checkpointPath],
                Timeout: Timeout),
            cancellationToken);
        return checkpointPath;
    }
}
