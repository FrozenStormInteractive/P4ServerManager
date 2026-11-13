using P4ServerManager.Backup.Manifests;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Refuses a restore that cannot fit, before anything is touched: before p4d stops, for a restore over an instance.
/// Where each file goes is known only once the database is restored, so this checks what is sure to be written. A new
/// instance takes every file of the point in its server directory. A restore over an instance downloads at least the
/// checkpoint and the journals next to its root: its archives may reuse the files it has, and the restore of the files
/// checks them again once placed.
/// </summary>
public sealed class CheckFreeSpaceStep(IVolumeSpaceReader volumeSpaceReader) : IOperationStep<RestoreContext>
{
    public string Key => "check-free-space";

    public string Title => "Check the free space";

    public Task<string?> RunAsync(
        RestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BackupManifestEntry> entries = context.RequireManifest().Entries;
        string rootPath = Path.GetFullPath(context.Instance.RootPath);
        (string folder, long needed) = context.ReplacesData
            ? (rootPath, entries.Where(e => BackupFileRoles.IsMetadata(e.Role)).Sum(e => e.Size))
            : (ServerLayout.GetServerDirectory(rootPath) ?? rootPath, entries.Sum(e => e.Size));

        string volume = volumeSpaceReader.GetVolume(folder);
        progress.Report(new OperationStepProgress(volume, null));
        if (volumeSpaceReader.GetAvailableFreeSpace(volume) is not { } available)
        {
            return Task.FromResult<string?>($"The free space of {volume} cannot be read: checked once the files are placed");
        }

        if (available < needed)
        {
            throw new InstanceOperationException(
                $"{volume} has {ByteSize.Format(available)} free, and the restore needs at least {ByteSize.Format(needed)} there.");
        }
        return Task.FromResult<string?>(
            $"{ByteSize.Format(needed)} needed at least, {ByteSize.Format(available)} free on {volume}");
    }
}
