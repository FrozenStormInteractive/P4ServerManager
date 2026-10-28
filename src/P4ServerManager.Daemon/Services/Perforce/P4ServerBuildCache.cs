using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Builds;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Perforce;

/// <summary>
/// Keeps the p4d builds: each one compressed in the <see cref="P4ServerBuildArchive"/>, and extracted in
/// <c>builds/{platform}/{version}/{build}/</c> while an instance needs it. Builds come from the Perforce FTP, which only
/// keeps the latest patch of a release, or are imported; the exact build is read from <c>p4d -V</c>.
/// </summary>
public sealed class P4ServerBuildCache(
    PerforceFtpClient ftpClient,
    P4ServerBuildArchive archive,
    P4ServerPlatformDefinition platformDefinition,
    DaemonPaths paths,
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    IProcessRunner processRunner,
    OperationTracker operationTracker,
    BuildNotifier notifier,
    TimeProvider timeProvider,
    ILogger<P4ServerBuildCache> logger) : IP4ServerBuildCache
{
    // Every change to the builds of a release holds its lock: two instances created on the same release never download
    // it twice, and a build is never removed while it is being extracted.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> releaseLocks = new();

    public string GetP4dPath(P4ServerBuild build)
    {
        return Path.Combine(GetDirectory(build), platformDefinition.P4dFileName);
    }

    public string GetP4Path(P4ServerBuild build)
    {
        return Path.Combine(GetDirectory(build), platformDefinition.P4FileName);
    }

    public async Task<P4ServerBuild> EnsureAsync(
        P4ServerRelease release,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        return await WithReleaseLockAsync(release.Name, cancellationToken, async () =>
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            IReadOnlyDictionary<string, string>? checksums;
            try
            {
                checksums = await ftpClient.GetChecksumsAsync(release, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                P4ServerBuild? newest = await FindNewestAsync(db, release, cancellationToken);
                if (newest is null)
                {
                    throw;
                }
                logger.LogWarning(exception, "The Perforce FTP could not be read, using {Release} from the archive", release);
                return await EnsureExtractedLockedAsync(db, newest, progress, cancellationToken);
            }

            if (checksums is null)
            {
                // The Perforce FTP no longer publishes the release, but the archive may still have it.
                P4ServerBuild newest = await FindNewestAsync(db, release, cancellationToken)
                    ?? throw new P4ServerBuildException($"Release {release} has no p4d for {platformDefinition.Name}.");
                return await EnsureExtractedLockedAsync(db, newest, progress, cancellationToken);
            }

            string p4dHash = GetChecksum(checksums, platformDefinition.P4dFileName, release);
            P4ServerBuild? known = await db.Builds.FirstOrDefaultAsync(
                b => b.Platform == platformDefinition.Name && b.Release == release.Name && b.Sha256 == p4dHash,
                cancellationToken);
            if (known is not null)
            {
                logger.LogInformation("Using p4d {Version}/{Build}", known.Version, known.BuildNumber);
                return await EnsureExtractedLockedAsync(db, known, progress, cancellationToken);
            }

            return await DownloadLockedAsync(db, release, checksums, extract: true, progress, cancellationToken);
        });
    }

    public async Task<P4ServerBuild> EnsureAsync(
        Guid buildId,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        string release = await GetReleaseAsync(buildId, cancellationToken);
        return await WithReleaseLockAsync(release, cancellationToken, async () =>
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerBuild build = await db.Builds.FirstOrDefaultAsync(b => b.Id == buildId, cancellationToken)
                ?? throw new KeyNotFoundException($"No p4d build has the identifier {buildId}.");
            return await EnsureExtractedLockedAsync(db, build, progress, cancellationToken);
        });
    }

    /// <summary>
    /// Downloads the current build of a release from the Perforce FTP into the archive, unless the daemon already has
    /// it. The build is not extracted.
    /// </summary>
    /// <returns>The build and whether it was downloaded now, or null when the release has no build for this platform.</returns>
    public async Task<(P4ServerBuild Build, bool Downloaded)?> AcquireAsync(
        P4ServerRelease release,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        return await WithReleaseLockAsync<(P4ServerBuild, bool)?>(release.Name, cancellationToken, async () =>
        {
            IReadOnlyDictionary<string, string>? checksums = await ftpClient.GetChecksumsAsync(release, cancellationToken);
            if (checksums is null
                || !checksums.TryGetValue(platformDefinition.P4dFileName, out string? p4dHash)
                || !checksums.ContainsKey(platformDefinition.P4FileName))
            {
                return null;
            }

            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerBuild? known = await db.Builds.FirstOrDefaultAsync(
                b => b.Platform == platformDefinition.Name && b.Release == release.Name && b.Sha256 == p4dHash,
                cancellationToken);
            if (known is not null)
            {
                return (known, false);
            }

            P4ServerBuild build = await DownloadLockedAsync(db, release, checksums, extract: false, progress, cancellationToken);
            return (build, true);
        });
    }

    /// <summary>
    /// Adds a build imported by a user to the archive: p4d and p4 in <paramref name="stagingDirectory"/>, which the
    /// caller deletes afterwards. The build is not extracted.
    /// </summary>
    /// <exception cref="P4ServerBuildException">The files are not a p4d and a p4 for this platform.</exception>
    /// <exception cref="P4ServerBuildConflictException">The daemon already has this build.</exception>
    public async Task<P4ServerBuild> ImportAsync(string stagingDirectory, CancellationToken cancellationToken)
    {
        string p4dPath = Path.Combine(stagingDirectory, platformDefinition.P4dFileName);
        string p4Path = Path.Combine(stagingDirectory, platformDefinition.P4FileName);
        P4ServerBuildArchive.MakeExecutable(p4dPath);
        P4ServerBuildArchive.MakeExecutable(p4Path);

        P4ServerVersionInfo versionInfo = await ReadVersionAsync(p4dPath, cancellationToken);
        if (!P4ServerRelease.TryParseVersion(versionInfo.Version, out P4ServerRelease release))
        {
            throw new P4ServerBuildException($"p4d reports version {versionInfo.Version}, which is not a known release.");
        }
        string p4dHash = await P4ServerBuildArchive.HashAsync(p4dPath, cancellationToken);
        string p4Hash = await P4ServerBuildArchive.HashAsync(p4Path, cancellationToken);

        return await WithReleaseLockAsync(release.Name, cancellationToken, async () =>
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            bool exists = await db.Builds.AnyAsync(
                b => b.Platform == platformDefinition.Name
                    && b.Version == versionInfo.Version
                    && b.BuildNumber == versionInfo.BuildNumber,
                cancellationToken);
            if (exists)
            {
                throw new P4ServerBuildConflictException(
                    $"p4d {versionInfo.Version}/{versionInfo.BuildNumber} is already in the archive.");
            }

            return await AddLockedAsync(
                db,
                stagingDirectory,
                release,
                versionInfo,
                p4dHash,
                p4Hash,
                imported: true,
                extract: false,
                progress: null,
                cancellationToken);
        });
    }

    /// <summary>
    /// Removes a build: its archive, its extracted files and its row.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No build has this identifier.</exception>
    /// <exception cref="P4ServerBuildConflictException">An instance uses the build.</exception>
    public async Task DeleteAsync(Guid buildId, CancellationToken cancellationToken)
    {
        string release = await GetReleaseAsync(buildId, cancellationToken);
        await WithReleaseLockAsync(release, cancellationToken, async () =>
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerBuild build = await db.Builds
                .Include(b => b.Instances)
                .FirstOrDefaultAsync(b => b.Id == buildId, cancellationToken)
                ?? throw new KeyNotFoundException($"No p4d build has the identifier {buildId}.");
            if (build.Instances.Count > 0)
            {
                throw new P4ServerBuildConflictException(
                    $"p4d {build.Version}/{build.BuildNumber} is used by {string.Join(", ", build.Instances.Select(i => i.Name))}.");
            }

            archive.Delete(build);
            DeleteDirectory(GetDirectory(build));
            db.Builds.Remove(build);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Removed p4d {Version}/{Build}", build.Version, build.BuildNumber);
            return true;
        });
        await notifier.BuildDeletedAsync(buildId);
    }

    /// <summary>
    /// Deletes the extracted files of a build that no instance uses, when it is archived. Nothing is deleted while an
    /// operation runs on an instance: an upgrade extracts its target build before it pins the instance to it.
    /// </summary>
    /// <returns>Whether the files were deleted.</returns>
    public async Task<bool> RemoveExtractedAsync(Guid buildId, CancellationToken cancellationToken)
    {
        string release = await GetReleaseAsync(buildId, cancellationToken);
        bool removed = await WithReleaseLockAsync(release, cancellationToken, async () =>
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerBuild? build = await db.Builds
                .Include(b => b.Instances)
                .FirstOrDefaultAsync(b => b.Id == buildId, cancellationToken);
            if (build is null
                || !build.IsExtracted
                || build.ArchiveSize is null
                || !archive.Exists(build)
                || build.Instances.Count > 0
                || operationTracker.HasRunningOperation())
            {
                return false;
            }

            DeleteDirectory(GetDirectory(build));
            build.IsExtracted = false;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Removed the unused extracted p4d {Version}/{Build}", build.Version, build.BuildNumber);
            return true;
        });
        if (removed)
        {
            await notifier.BuildChangedAsync(buildId);
        }
        return removed;
    }

    /// <summary>
    /// Archives a build that is only extracted, such as one cached before the archive existed.
    /// </summary>
    /// <returns>Whether the build was archived now.</returns>
    public async Task<bool> ArchiveAsync(
        Guid buildId,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        string release = await GetReleaseAsync(buildId, cancellationToken);
        bool archived = await WithReleaseLockAsync(release, cancellationToken, async () =>
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerBuild? build = await db.Builds.FirstOrDefaultAsync(b => b.Id == buildId, cancellationToken);
            if (build is null || (build.ArchiveSize is not null && archive.Exists(build)) || !HasExtractedFiles(build))
            {
                return false;
            }

            build.P4Sha256 ??= await P4ServerBuildArchive.HashAsync(GetP4Path(build), cancellationToken);
            build.ArchiveSize = await archive.CreateAsync(
                build,
                GetDirectory(build),
                ReportStage(progress, BuildProgressStage.Compressing, build),
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        });
        if (archived)
        {
            await notifier.BuildChangedAsync(buildId);
        }
        return archived;
    }

    /// <summary>
    /// Brings the rows of the builds in line with the disk: whether each one is extracted and archived.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<P4ServerBuild> builds = await db.Builds
            .Where(b => b.Platform == platformDefinition.Name)
            .ToListAsync(cancellationToken);
        foreach (P4ServerBuild build in builds)
        {
            build.IsExtracted = HasExtractedFiles(build);
            build.ArchiveSize = archive.Exists(build) ? new FileInfo(archive.GetPath(build)).Length : null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<P4ServerBuild> EnsureExtractedLockedAsync(
        DaemonDbContext db,
        P4ServerBuild build,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (HasExtractedFiles(build))
        {
            if (!build.IsExtracted)
            {
                build.IsExtracted = true;
                await db.SaveChangesAsync(cancellationToken);
            }
            return build;
        }

        string directory = GetDirectory(build);
        // Left over from an interrupted extraction.
        DeleteDirectory(directory);
        await archive.ExtractAsync(
            build,
            directory,
            ReportStage(progress, BuildProgressStage.Extracting, build),
            cancellationToken);
        build.IsExtracted = true;
        await db.SaveChangesAsync(cancellationToken);
        await notifier.BuildChangedAsync(build.Id);
        return build;
    }

    private async Task<P4ServerBuild> DownloadLockedAsync(
        DaemonDbContext db,
        P4ServerRelease release,
        IReadOnlyDictionary<string, string> checksums,
        bool extract,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        string p4dHash = GetChecksum(checksums, platformDefinition.P4dFileName, release);
        string p4Hash = GetChecksum(checksums, platformDefinition.P4FileName, release);

        string downloadDirectory = CreateStagingDirectory();
        try
        {
            string p4dPath = Path.Combine(downloadDirectory, platformDefinition.P4dFileName);
            string p4Path = Path.Combine(downloadDirectory, platformDefinition.P4FileName);
            await DownloadAsync(release, platformDefinition.P4dFileName, p4dPath, p4dHash, progress, cancellationToken);
            await DownloadAsync(release, platformDefinition.P4FileName, p4Path, p4Hash, progress, cancellationToken);

            P4ServerVersionInfo versionInfo = await ReadVersionAsync(p4dPath, cancellationToken);
            return await AddLockedAsync(
                db,
                downloadDirectory,
                release,
                versionInfo,
                p4dHash,
                p4Hash,
                imported: false,
                extract,
                progress,
                cancellationToken);
        }
        finally
        {
            DeleteDirectory(downloadDirectory);
        }
    }

    /// <summary>
    /// Archives p4d and p4 of <paramref name="sourceDirectory"/>, moves them to the build directory when
    /// <paramref name="extract"/> is set, then saves the build.
    /// </summary>
    private async Task<P4ServerBuild> AddLockedAsync(
        DaemonDbContext db,
        string sourceDirectory,
        P4ServerRelease release,
        P4ServerVersionInfo versionInfo,
        string p4dHash,
        string p4Hash,
        bool imported,
        bool extract,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        P4ServerBuild? build = await db.Builds.FirstOrDefaultAsync(
            b => b.Platform == platformDefinition.Name
                && b.Version == versionInfo.Version
                && b.BuildNumber == versionInfo.BuildNumber,
            cancellationToken);
        if (build is null)
        {
            build = new P4ServerBuild
            {
                Id = Guid.NewGuid(),
                Release = release.Name,
                Version = versionInfo.Version,
                BuildNumber = versionInfo.BuildNumber,
                Platform = platformDefinition.Name,
                Sha256 = p4dHash,
            };
            db.Builds.Add(build);
        }
        build.Release = release.Name;
        build.Sha256 = p4dHash;
        build.P4Sha256 = p4Hash;
        build.Imported = imported;
        build.DownloadedAt = timeProvider.GetUtcNow();
        build.ArchiveSize = await archive.CreateAsync(
            build,
            sourceDirectory,
            ReportStage(progress, BuildProgressStage.Compressing, build),
            cancellationToken);

        if (extract)
        {
            string targetDirectory = GetDirectory(build);
            // Left over from an incomplete download: the database has no valid row for it.
            DeleteDirectory(targetDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
            Directory.Move(sourceDirectory, targetDirectory);
        }
        build.IsExtracted = extract || HasExtractedFiles(build);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Added p4d {Version}/{Build} of {Release} to the archive",
            build.Version,
            build.BuildNumber,
            release);
        await notifier.BuildChangedAsync(build.Id);
        return build;
    }

    private async Task DownloadAsync(
        P4ServerRelease release,
        string fileName,
        string destinationPath,
        string expectedHash,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        int? lastPercent = null;
        bool reported = false;
        InlineProgress<DownloadProgress>? downloadProgress = progress is null
            ? null
            : new InlineProgress<DownloadProgress>(p =>
            {
                int? percent = p.TotalBytes is > 0 ? (int)(p.BytesReceived * 100 / p.TotalBytes.Value) : null;
                if (!reported || percent != lastPercent)
                {
                    reported = true;
                    lastPercent = percent;
                    progress.Report(new BuildProgress(BuildProgressStage.Downloading, release.Name, fileName, null, percent));
                }
            });

        logger.LogInformation("Downloading {FileName} from {Release}", fileName, release);
        await ftpClient.DownloadAsync(release, fileName, destinationPath, downloadProgress, cancellationToken);

        if (await P4ServerBuildArchive.HashAsync(destinationPath, cancellationToken) != expectedHash)
        {
            throw new P4ServerBuildException(
                $"The checksum of {fileName} from {release} does not match SHA256SUMS. The download was discarded.");
        }
        P4ServerBuildArchive.MakeExecutable(destinationPath);
    }

    private async Task<P4ServerVersionInfo> ReadVersionAsync(string p4dPath, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                new ProcessCommand(p4dPath, ["-V"], Timeout: TimeSpan.FromSeconds(30)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Such as a file that is not an executable for this platform.
            throw new P4ServerBuildException(
                $"p4d could not run on {platformDefinition.Name}: {exception.Message}");
        }
        if (!result.Succeeded || !P4ServerVersionParser.TryParse(result.StandardOutput, out P4ServerVersionInfo? versionInfo))
        {
            throw new P4ServerBuildException(
                $"Could not read the version of p4d, it may not be a p4d for {platformDefinition.Name}: {result.ErrorText}");
        }
        return versionInfo;
    }

    private async Task<P4ServerBuild?> FindNewestAsync(
        DaemonDbContext db,
        P4ServerRelease release,
        CancellationToken cancellationToken)
    {
        List<P4ServerBuild> builds = await db.Builds
            .Where(b => b.Platform == platformDefinition.Name && b.Release == release.Name)
            .ToListAsync(cancellationToken);
        return builds.OrderByDescending(b => P4ServerBuildNumber.Parse(b.BuildNumber)).FirstOrDefault();
    }

    private async Task<string> GetReleaseAsync(Guid buildId, CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Builds
            .Where(b => b.Id == buildId)
            .Select(b => b.Release)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"No p4d build has the identifier {buildId}.");
    }

    private async Task<T> WithReleaseLockAsync<T>(string release, CancellationToken cancellationToken, Func<Task<T>> action)
    {
        SemaphoreSlim releaseLock = releaseLocks.GetOrAdd(release, static _ => new SemaphoreSlim(1, 1));
        await releaseLock.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            releaseLock.Release();
        }
    }

    private static InlineProgress<int>? ReportStage(
        IProgress<BuildProgress>? progress,
        BuildProgressStage stage,
        P4ServerBuild build)
    {
        return progress is null
            ? null
            : new InlineProgress<int>(percent =>
                progress.Report(new BuildProgress(
                    stage,
                    build.Release,
                    null,
                    $"{build.Version}/{build.BuildNumber}",
                    percent)));
    }

    /// <summary>
    /// Creates an empty folder under <c>builds/.tmp</c>, on the same volume as the build directories so that a build
    /// moves there without a copy.
    /// </summary>
    public string CreateStagingDirectory()
    {
        string directory = Path.Combine(paths.BuildsDirectory, ".tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private bool HasExtractedFiles(P4ServerBuild build)
    {
        return File.Exists(GetP4dPath(build)) && File.Exists(GetP4Path(build));
    }

    private string GetChecksum(IReadOnlyDictionary<string, string> checksums, string fileName, P4ServerRelease release)
    {
        return checksums.TryGetValue(fileName, out string? hash)
            ? hash
            : throw new P4ServerBuildException($"Release {release} has no {fileName} for {platformDefinition.Name}.");
    }

    private string GetDirectory(P4ServerBuild build)
    {
        return paths.GetBuildDirectory(build.Platform, build.Version, build.BuildNumber);
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>
/// What the cache does to get a build ready.
/// </summary>
public enum BuildProgressStage
{
    /// <summary>Downloading p4d or p4 from Perforce.</summary>
    Downloading,

    /// <summary>Compressing a new build into the archive.</summary>
    Compressing,

    /// <summary>Extracting a build from the archive so that an instance can run it.</summary>
    Extracting,
}

/// <param name="Stage">What the cache does.</param>
/// <param name="Release">Release of the build, for example <c>r25.1</c>.</param>
/// <param name="FileName">File being downloaded, <c>p4d</c> or <c>p4</c>, while downloading.</param>
/// <param name="Build">Version and build, for example <c>2025.1/2850123</c>, once known.</param>
/// <param name="Percent">Progress of the stage, when known.</param>
public readonly record struct BuildProgress(
    BuildProgressStage Stage,
    string Release,
    string? FileName,
    string? Build,
    int? Percent)
{
    /// <summary>
    /// The stage in words, such as <c>Compressing p4d 2025.1/2850123 into the archive</c>, without the percent.
    /// </summary>
    public string Description => Stage switch
    {
        BuildProgressStage.Compressing => $"Compressing p4d {Build} into the archive",
        BuildProgressStage.Extracting => $"Extracting p4d {Build} from the archive",
        _ => $"Downloading {FileName} of {Release}",
    };
}

/// <summary>
/// Gets the p4d builds that instances run. A build is only removed from disk once no instance uses it, and stays in
/// the archive until the user or the retention policy removes it.
/// </summary>
public interface IP4ServerBuildCache
{
    /// <summary>
    /// Returns the latest build of a release, extracted: the one of the Perforce FTP, downloaded first when needed. When
    /// the FTP cannot be read or no longer publishes the release, the newest build of the archive.
    /// </summary>
    Task<P4ServerBuild> EnsureAsync(
        P4ServerRelease release,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns an exact build, extracted from the archive first when needed.
    /// </summary>
    Task<P4ServerBuild> EnsureAsync(
        Guid buildId,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken);

    string GetP4dPath(P4ServerBuild build);

    string GetP4Path(P4ServerBuild build);
}
