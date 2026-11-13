using System.Globalization;

using P4ServerManager.Backup.Manifests;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Backups.Destinations;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Steps;

/// <summary>
/// Opens the repository of each destination and reads the latest point of the instance there. An incremental backup
/// needs that point to end right before the journal p4d writes to: otherwise the destination misses journals, and only
/// a full backup can go there. A destination that cannot take the backup fails alone; the backup stops only when none
/// can, before it touches the server.
/// </summary>
public sealed class OpenBackupDestinationsStep(
    BackupDestinationService destinationService,
    ServerFileLocator locator,
    BackupRunNotifier runNotifier,
    ILogger<OpenBackupDestinationsStep> logger) : IOperationStep<BackupContext>
{
    public string Key => "open-backup-destinations";

    public string Title => "Open the backup destinations";

    public async Task<string?> RunAsync(
        BackupContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        int? journalCounter = context.Kind == BackupKind.Incremental
            ? await locator.ReadJournalCounterAsync(context.Instance, cancellationToken)
            : null;

        foreach (BackupDestinationState state in context.Destinations)
        {
            progress.Report(new OperationStepProgress(state.Destination.Name, null));
            try
            {
                state.Opened = await destinationService.OpenAsync(state.Destination, cancellationToken);
                state.Base = await ReadLatestPointAsync(state.Opened, context.Instance.Id, cancellationToken);
                foreach (BackupManifestEntry entry in state.Base?.Entries ?? [])
                {
                    state.KnownObjects.Add(entry.Object);
                }

                if (journalCounter is { } counter && CheckChain(state.Base, counter) is { } problem)
                {
                    state.Fail(problem);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Backup destination {Destination} cannot take the backup of instance {Name}",
                    state.Destination.Name,
                    context.Instance.Name);
                state.Fail(exception.Message);
            }
        }

        // An incremental backup builds on the checkpoint of the latest point of its destinations.
        if (context.Kind == BackupKind.Incremental
            && context.Destinations.FirstOrDefault(d => !d.Failed)?.Base is { } basePoint
            && TryGetNumber(basePoint.Header.Labels, BackupLabels.CheckpointNumber, out int baseCheckpoint))
        {
            context.Run.CheckpointNumber = baseCheckpoint;
        }

        await runNotifier.SaveAsync(context.Db, context.Run);
        int ready = context.Destinations.Count(d => !d.Failed);
        if (ready == 0)
        {
            throw new InstanceOperationException(
                "No destination can take the backup. "
                + string.Join(" ", context.Destinations.Select(d => $"{d.Destination.Name}: {d.Record.Error}")));
        }
        return $"{ready} of {context.Destinations.Count} destinations ready";
    }

    /// <summary>
    /// Why an incremental backup cannot build on <paramref name="basePoint"/>, if it cannot.
    /// </summary>
    /// <param name="basePoint">The latest point of the instance at the destination.</param>
    /// <param name="journalCounter">The journal p4d writes to: the rotation gives it this number.</param>
    public static string? CheckChain(BackupManifest? basePoint, int journalCounter)
    {
        if (basePoint is null)
        {
            return "The destination has no backup of this instance yet. Make a full backup first.";
        }

        Dictionary<string, string> labels = basePoint.Header.Labels;
        if (!TryGetNumber(labels, BackupLabels.CheckpointNumber, out int checkpoint))
        {
            return "The latest backup at the destination does not tell its checkpoint. Make a full backup.";
        }

        int expected = TryGetNumber(labels, BackupLabels.JournalNumber, out int journal) ? journal + 1 : checkpoint;
        if (journalCounter == expected)
        {
            return null;
        }
        return journalCounter > expected
            ? $"The destination misses journals {expected} to {journalCounter - 1}, rotated since its latest backup. Make a full backup."
            : $"The server writes journal {journalCounter}, older than the latest backup at the destination. Make a full backup.";
    }

    private static bool TryGetNumber(Dictionary<string, string> labels, string name, out int number)
    {
        number = 0;
        return labels.TryGetValue(name, out string? value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static async Task<BackupManifest?> ReadLatestPointAsync(
        OpenedBackupDestination opened,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        BackupSnapshotInfo? latest = null;
        await foreach (BackupSnapshotInfo snapshot in opened.Repository.ListSnapshotsAsync(instanceId, cancellationToken))
        {
            latest = snapshot;
        }
        return latest is null ? null : await opened.Repository.ReadManifestAsync(latest.Key, cancellationToken);
    }
}
