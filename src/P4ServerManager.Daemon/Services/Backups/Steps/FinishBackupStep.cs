using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Steps;

/// <summary>
/// Keeps the index of the files for the next backup, and fails the backup when it reached no destination.
/// </summary>
public sealed class FinishBackupStep(DaemonPaths paths) : IOperationStep<BackupContext>
{
    public string Key => "finish-backup";

    public string Title => "Finish the backup";

    public async Task<string?> RunAsync(
        BackupContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        if (context.Index is not null)
        {
            await context.Index.SaveAsync(
                paths.GetBackupIndexPath(context.Instance.Id),
                [.. context.MetadataFiles, .. context.ServerFiles],
                cancellationToken);
        }

        int succeeded = context.Destinations.Count(d => d.Record.Status == BackupDestinationStatus.Succeeded);
        if (succeeded == 0)
        {
            throw new InstanceOperationException(
                "The backup reached no destination. "
                + string.Join(" ", context.Destinations.Select(d => $"{d.Destination.Name}: {d.Record.Error}")));
        }
        return $"Backed up to {succeeded} of {context.Destinations.Count} destinations";
    }
}
