using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.UnicodeConversion;

/// <summary>
/// Dumps the database to a compressed checkpoint in the folder of the instance: it is the only way back, as a server in
/// Unicode mode cannot leave it. Each attempt has its own file, so a retry never replaces an earlier checkpoint.
/// <c>-jd</c> leaves the journal as it is, unlike <c>-jc</c>.
/// </summary>
public sealed class SaveConversionCheckpointStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    TimeProvider timeProvider) : IOperationStep<UnicodeConversionContext>
{
    public string Key => "save-checkpoint";

    public string Title => "Save a checkpoint";

    public async Task<string?> RunAsync(
        UnicodeConversionContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        string checkpointPath = paths.GetUnicodeCheckpointPath(instance.Id, timeProvider.GetUtcNow());
        Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);
        await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(context.Build),
                [.. P4ServerLauncher.GetRootArguments(instance), "-z", "-jd", checkpointPath],
                Timeout: SaveCheckpointStep.Timeout),
            cancellationToken);
        return checkpointPath;
    }
}
