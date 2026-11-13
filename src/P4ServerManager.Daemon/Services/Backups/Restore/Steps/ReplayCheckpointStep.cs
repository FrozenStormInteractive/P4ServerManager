using System.Globalization;

using P4ServerManager.Backup.Manifests;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Rebuilds the database in the empty root: replays the checkpoint of the point, then each journal rotated after it,
/// in order, then upgrades the database to the build of the instance.
/// </summary>
public sealed class ReplayCheckpointStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner) : IOperationStep<RestoreContext>
{
    private const string CheckpointPrefix = "checkpoint.";
    private const string CheckpointMarker = ".ckp.";

    public string Key => "replay-checkpoint";

    public string Title => "Replay the checkpoint and the journals";

    /// <summary>
    /// The checkpoint of a point, and the journals to replay after it, in order.
    /// </summary>
    /// <exception cref="InstanceOperationException">The point has no checkpoint whose name tells its number.</exception>
    public static IReadOnlyList<(BackupManifestEntry Entry, JournalFile File)> GetReplayOrder(BackupManifest manifest)
    {
        BackupManifestEntry checkpoint = manifest.Entries.FirstOrDefault(e => e.Role == BackupFileRoles.Checkpoint)
            ?? throw new InstanceOperationException("The backup point has no checkpoint.");
        string checkpointName = Path.GetFileName(checkpoint.Path);
        int marker = checkpointName.IndexOf(CheckpointMarker, StringComparison.Ordinal);
        string? prefix = checkpointName.StartsWith(CheckpointPrefix, StringComparison.Ordinal) || marker <= 0
            ? null
            : checkpointName[..marker];
        JournalFile checkpointFile = ServerFileLocator.ParseJournalFileName(checkpointName, prefix)
            ?? throw new InstanceOperationException($"The number of checkpoint {checkpointName} cannot be read.");

        List<(BackupManifestEntry, JournalFile)> journals = [];
        foreach (BackupManifestEntry entry in manifest.Entries.Where(e => e.Role == BackupFileRoles.Journal))
        {
            JournalFile? journal = ServerFileLocator.ParseJournalFileName(Path.GetFileName(entry.Path), prefix);
            if (journal is null)
            {
                throw new InstanceOperationException($"The number of journal {entry.Path} cannot be read.");
            }
            // The journals rotated before the checkpoint are already in it.
            if (journal.Number >= checkpointFile.Number)
            {
                journals.Add((entry, journal));
            }
        }

        return [(checkpoint, checkpointFile), .. journals.OrderBy(j => j.Item2.Number)];
    }

    public async Task<string?> RunAsync(
        RestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerBuild build = context.Instance.Build
            ?? throw new InvalidOperationException("The instance has no p4d build yet.");
        BackupManifest manifest = context.RequireManifest();
        BackupPoint point = new(
            new BackupSnapshotInfo(context.Run.SnapshotKey, manifest.Header.SourceId, manifest.Header.CreatedAt, manifest.Header.Id),
            manifest.Header);
        if (OpenRestoreSourceStep.IsNewerThan(point, build))
        {
            throw new InstanceOperationException(
                $"The point was made with p4d {point.P4dVersion}, newer than {build.Version}/{build.BuildNumber}: "
                + "this build cannot read its checkpoint.");
        }

        string p4dPath = buildCache.GetP4dPath(build);
        // The database is rebuilt from the point, so the replay needs no journal: the folder of the journals may not
        // exist yet. The upgrade writes one, which p4d requires, next to the point.
        IReadOnlyList<string> rootArguments = P4ServerLauncher.GetRootArguments(context.Instance);
        IReadOnlyList<string> noJournal = ["-J", "off"];
        IReadOnlyList<string> stagedJournal = ["-J", Path.Combine(context.StagingDirectory, "journal")];
        IReadOnlyList<(BackupManifestEntry Entry, JournalFile File)> order = GetReplayOrder(context.RequireManifest());

        for (int i = 0; i < order.Count; i++)
        {
            BackupManifestEntry entry = order[i].Entry;
            string path = RestoreMetadataStep.GetStagedPath(context, entry);
            progress.Report(new OperationStepProgress(Path.GetFileName(path), i * 100 / (order.Count + 1)));
            IReadOnlyList<string> replay = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? ["-z", "-jr", path]
                : ["-jr", path];
            await processRunner.RunCheckedAsync(
                new ProcessCommand(p4dPath, [.. rootArguments, .. noJournal, .. replay], Timeout: Timeout.InfiniteTimeSpan),
                cancellationToken);
        }

        progress.Report(new OperationStepProgress("p4d -xu", order.Count * 100 / (order.Count + 1)));
        await processRunner.RunCheckedAsync(
            new ProcessCommand(p4dPath, [.. rootArguments, .. stagedJournal, "-xu"], Timeout: Timeout.InfiniteTimeSpan),
            cancellationToken);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Checkpoint {order[0].File.Number} and {order.Count - 1} journals replayed");
    }
}
