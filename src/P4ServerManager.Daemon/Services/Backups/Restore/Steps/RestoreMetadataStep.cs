using P4ServerManager.Backup.Crypto;
using P4ServerManager.Backup.Manifests;
using P4ServerManager.Backup.Targets;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Downloads the checkpoint and the journals of the point next to the root, where the replay reads them. Each file is
/// checked against its size and SHA-256.
/// </summary>
public sealed class RestoreMetadataStep : IOperationStep<RestoreContext>
{
    private const string JournalsPrefix = "journals/";

    public string Key => "restore-metadata";

    public string Title => "Download the checkpoint and the journals";

    /// <summary>
    /// Where a file of the folder of the journals waits for the replay.
    /// </summary>
    public static string GetStagedPath(RestoreContext context, BackupManifestEntry entry)
    {
        string name = entry.Path.StartsWith(JournalsPrefix, StringComparison.Ordinal)
            ? entry.Path[JournalsPrefix.Length..]
            : string.Empty;
        if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal) || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InstanceOperationException($"The point has a {entry.Role} this server cannot write: {entry.Path}.");
        }
        return Path.Combine(context.StagingDirectory, name);
    }

    public async Task<string?> RunAsync(
        RestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        List<BackupManifestEntry> entries = context.RequireManifest().Entries
            .Where(e => BackupFileRoles.IsMetadata(e.Role))
            .ToList();
        long total = entries.Sum(e => e.Size);
        long done = 0;
        RestoreDirectories.CreatePrivate(context.StagingDirectory);
        foreach (BackupManifestEntry entry in entries)
        {
            progress.Report(new OperationStepProgress(entry.Path, total == 0 ? null : (int)(done * 100 / total)));
            try
            {
                await context.RequireOpened().Repository.RestoreFileAsync(
                    entry,
                    GetStagedPath(context, entry),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is BackupIntegrityException or BackupObjectNotFoundException)
            {
                throw new InstanceOperationException($"{entry.Path} cannot be restored: {exception.Message}");
            }
            done += entry.Size;
            context.Run.DownloadedBytes += entry.Size;
        }
        return $"{entries.Count} files, {ByteSize.Format(total)}";
    }
}
