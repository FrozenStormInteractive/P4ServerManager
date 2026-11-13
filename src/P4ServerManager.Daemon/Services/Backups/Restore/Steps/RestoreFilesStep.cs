using System.Collections.Concurrent;
using System.Security.Cryptography;

using P4ServerManager.Backup.Crypto;
using P4ServerManager.Backup.Targets;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Writes the files of the point where the plan puts them: the checkpoint and the journals move from the staging folder,
/// the archives and the files of the root are downloaded and checked. When the point replaces the data of the
/// instance, a file the previous data had unchanged is moved back rather than downloaded. The index of the files is
/// written for the next backup.
/// </summary>
public sealed class RestoreFilesStep(
    DaemonPaths paths,
    RestoreRunNotifier runNotifier,
    IVolumeSpaceReader volumeSpaceReader) : IOperationStep<RestoreContext>
{
    private const int Workers = 4;
    private const int BufferSize = 1024 * 1024;

    public string Key => "restore-files";

    public string Title => "Restore the files";

    public async Task<string?> RunAsync(
        RestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        RestorePlan plan = context.Plan ?? throw new InvalidOperationException("The files are not placed yet.");
        RestoreSetAside? setAside = context.SetAside;

        // A folder of the restored layout that the current data did not use may hold files: it is set aside too.
        if (setAside is not null)
        {
            foreach (string directory in plan.Directories.Where(d => !setAside.Covers(d)))
            {
                await setAside.MoveAsideAsync(directory, cancellationToken);
            }
        }

        CheckFreeSpace(plan, setAside);
        foreach (string directory in plan.Directories)
        {
            Directory.CreateDirectory(directory);
        }

        ConcurrentBag<BackupFile> restored = [];
        foreach (RestoreFile file in plan.Metadata)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            File.Move(RestoreMetadataStep.GetStagedPath(context, file.Entry), file.Path, overwrite: true);
            restored.Add(ToIndexed(file));
        }

        BackupFileIndex index = await BackupFileIndex.LoadAsync(paths.GetBackupIndexPath(context.Instance.Id), cancellationToken);
        long total = plan.Files.Sum(f => f.Entry.Size);
        long done = 0;
        long downloaded = 0;
        int downloadedFiles = 0;
        int reused = 0;
        object progressLock = new();
        await Parallel.ForEachAsync(
            plan.Files,
            new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                if (await TryReuseAsync(setAside, index, file, token))
                {
                    Interlocked.Increment(ref reused);
                }
                else
                {
                    try
                    {
                        await context.RequireOpened().Repository.RestoreFileAsync(file.Entry, file.Path, token);
                    }
                    catch (Exception exception) when (exception is BackupIntegrityException or BackupObjectNotFoundException)
                    {
                        throw new InstanceOperationException($"{file.Entry.Path} cannot be restored: {exception.Message}");
                    }
                    Interlocked.Add(ref downloaded, file.Entry.Size);
                    Interlocked.Increment(ref downloadedFiles);
                }

                if (IsInSslFolder(context, file.Path))
                {
                    RestoreDirectories.CreatePrivate(Path.GetDirectoryName(file.Path)!);
                    RestoreDirectories.MakePrivate(file.Path);
                }
                restored.Add(ToIndexed(file));

                long completed = Interlocked.Add(ref done, file.Entry.Size);
                lock (progressLock)
                {
                    progress.Report(new OperationStepProgress(
                        file.Entry.Path,
                        total == 0 ? null : (int)(completed * 100 / total)));
                }
            });

        if (Directory.Exists(context.StagingDirectory))
        {
            RestoreSetAside.DeleteDirectory(context.StagingDirectory);
        }
        await index.SaveAsync(paths.GetBackupIndexPath(context.Instance.Id), restored, cancellationToken);

        context.Run.DownloadedBytes += downloaded;
        context.Run.ReusedFiles = reused;
        await runNotifier.SaveAsync(context.Db, context.Run);
        return reused > 0
            ? $"{downloadedFiles} files downloaded ({ByteSize.Format(downloaded)}), {reused} unchanged files kept"
            : $"{downloadedFiles} files downloaded ({ByteSize.Format(downloaded)})";
    }

    /// <summary>
    /// Moves the previous version of a file back when it has the content of the point.
    /// </summary>
    private static async Task<bool> TryReuseAsync(
        RestoreSetAside? setAside,
        BackupFileIndex index,
        RestoreFile file,
        CancellationToken cancellationToken)
    {
        if (setAside?.FindPrevious(file.Path) is not { } previous)
        {
            return false;
        }

        FileInfo info = new(previous);
        if (!info.Exists || info.Length != file.Entry.Size)
        {
            return false;
        }

        string? sha256 = index.FindSha256(file.Path, info.Length, info.LastWriteTimeUtc);
        if (sha256 is null)
        {
            await using FileStream stream = new(
                previous,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        if (!string.Equals(sha256, file.Entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await setAside.ReuseAsync(previous, file.Path, cancellationToken);
        return true;
    }

    /// <summary>
    /// Checks that each volume has room for what is downloaded to it, by mount point: on Linux, the root of every path is
    /// <c>/</c>, whatever volume holds it. The volume of a file is the one of the folder of the plan that holds it. The
    /// files the previous data may give back are not counted.
    /// </summary>
    private void CheckFreeSpace(RestorePlan plan, RestoreSetAside? setAside)
    {
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Dictionary<string, string> volumes = plan.Directories
            .Distinct(comparer)
            .ToDictionary(directory => directory, volumeSpaceReader.GetVolume, comparer);
        Dictionary<string, long> needed = new(comparer);
        foreach (RestoreFile file in plan.Files)
        {
            if (setAside?.FindPrevious(file.Path) is { } previous
                && new FileInfo(previous) is { Exists: true } info
                && info.Length == file.Entry.Size)
            {
                continue;
            }

            string volume = volumes
                .Where(v => ServerFileLocator.Contains(v.Key, file.Path))
                .MaxBy(v => v.Key.Length)
                .Value
                ?? volumeSpaceReader.GetVolume(Path.GetDirectoryName(file.Path) ?? file.Path);
            needed[volume] = needed.GetValueOrDefault(volume) + file.Entry.Size;
        }

        foreach ((string volume, long bytes) in needed)
        {
            if (volumeSpaceReader.GetAvailableFreeSpace(volume) is not { } available)
            {
                continue;
            }
            if (available < bytes)
            {
                throw new InstanceOperationException(
                    $"{volume} has {ByteSize.Format(available)} free, and the restore needs {ByteSize.Format(bytes)} there.");
            }
        }
    }

    private static bool IsInSslFolder(RestoreContext context, string path)
    {
        return ServerFileLocator.Contains(ServerLayout.GetSslDirectory(context.Instance.RootPath), path);
    }

    private static BackupFile ToIndexed(RestoreFile file)
    {
        FileInfo info = new(file.Path);
        return new BackupFile(
            file.Entry.Role,
            file.Entry.Path,
            file.Path,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc.Ticks, TimeSpan.Zero),
            file.Entry.Sha256);
    }
}
