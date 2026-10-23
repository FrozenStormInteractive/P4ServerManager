using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Perforce;

/// <summary>
/// Downloads p4d and p4 from the Perforce FTP into <c>builds/{platform}/{version}/{build}/</c>. The FTP only keeps
/// the latest patch of a release, so the exact build is read from <c>p4d -V</c> once downloaded.
/// </summary>
public sealed class P4ServerBuildCache(
    PerforceFtpClient ftpClient,
    P4ServerPlatformDefinition platformDefinition,
    DaemonPaths paths,
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    IProcessRunner processRunner,
    TimeProvider timeProvider,
    ILogger<P4ServerBuildCache> logger) : IP4ServerBuildCache
{
    private readonly ConcurrentDictionary<P4ServerRelease, SemaphoreSlim> releaseLocks = new();

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
        IProgress<BuildDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Two instances created on the same release must not download it twice.
        SemaphoreSlim releaseLock = releaseLocks.GetOrAdd(release, static _ => new SemaphoreSlim(1, 1));
        await releaseLock.WaitAsync(cancellationToken);
        try
        {
            return await EnsureLockedAsync(release, progress, cancellationToken);
        }
        finally
        {
            releaseLock.Release();
        }
    }

    private async Task<P4ServerBuild> EnsureLockedAsync(
        P4ServerRelease release,
        IProgress<BuildDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> checksums = await ftpClient.GetChecksumsAsync(release, cancellationToken)
            ?? throw new P4ServerBuildException($"Release {release} has no p4d for {platformDefinition.Name}.");
        string p4dHash = GetChecksum(checksums, platformDefinition.P4dFileName, release);
        string p4Hash = GetChecksum(checksums, platformDefinition.P4FileName, release);

        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        P4ServerBuild? cached = await db.Builds.FirstOrDefaultAsync(
            b => b.Platform == platformDefinition.Name && b.Release == release.Name && b.Sha256 == p4dHash,
            cancellationToken);
        if (cached is not null && File.Exists(GetP4dPath(cached)) && File.Exists(GetP4Path(cached)))
        {
            logger.LogInformation("Using cached p4d {Version}/{Build}", cached.Version, cached.BuildNumber);
            return cached;
        }

        string downloadDirectory = Path.Combine(paths.BuildsDirectory, ".tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadDirectory);
        try
        {
            string p4dPath = Path.Combine(downloadDirectory, platformDefinition.P4dFileName);
            string p4Path = Path.Combine(downloadDirectory, platformDefinition.P4FileName);
            await DownloadAsync(release, platformDefinition.P4dFileName, p4dPath, p4dHash, progress, cancellationToken);
            await DownloadAsync(release, platformDefinition.P4FileName, p4Path, p4Hash, progress, cancellationToken);

            P4ServerVersionInfo versionInfo = await ReadVersionAsync(p4dPath, cancellationToken);
            string targetDirectory = paths.GetBuildDirectory(
                platformDefinition.Name,
                versionInfo.Version,
                versionInfo.BuildNumber);
            if (Directory.Exists(targetDirectory))
            {
                // Left over from an incomplete download: the database has no valid row for it.
                Directory.Delete(targetDirectory, recursive: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
            Directory.Move(downloadDirectory, targetDirectory);

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
            build.DownloadedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Cached p4d {Version}/{Build} from {Release}",
                build.Version,
                build.BuildNumber,
                release);
            return build;
        }
        finally
        {
            if (Directory.Exists(downloadDirectory))
            {
                Directory.Delete(downloadDirectory, recursive: true);
            }
        }
    }

    private async Task DownloadAsync(
        P4ServerRelease release,
        string fileName,
        string destinationPath,
        string expectedHash,
        IProgress<BuildDownloadProgress>? progress,
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
                    progress.Report(new BuildDownloadProgress(fileName, percent));
                }
            });

        logger.LogInformation("Downloading {FileName} from {Release}", fileName, release);
        await ftpClient.DownloadAsync(release, fileName, destinationPath, downloadProgress, cancellationToken);

        string actualHash;
        await using (FileStream stream = File.OpenRead(destinationPath))
        {
            actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        if (actualHash != expectedHash)
        {
            throw new P4ServerBuildException(
                $"The checksum of {fileName} from {release} does not match SHA256SUMS. The download was discarded.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                destinationPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private async Task<P4ServerVersionInfo> ReadVersionAsync(string p4dPath, CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            new ProcessCommand(p4dPath, ["-V"], Timeout: TimeSpan.FromSeconds(30)),
            cancellationToken);
        if (!result.Succeeded || !P4ServerVersionParser.TryParse(result.StandardOutput, out P4ServerVersionInfo? versionInfo))
        {
            throw new P4ServerBuildException($"Could not read the version of the downloaded p4d: {result.ErrorText}");
        }
        return versionInfo;
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
}

/// <param name="FileName">File being downloaded, <c>p4d</c> or <c>p4</c>.</param>
/// <param name="Percent">Progress of that file, when its size is known.</param>
public readonly record struct BuildDownloadProgress(string FileName, int? Percent);

/// <summary>
/// Keeps the downloaded p4d builds. They are never removed: they are what runs the instances.
/// </summary>
public interface IP4ServerBuildCache
{
    /// <summary>
    /// Returns the cached build of the latest patch of a release, downloading it first when needed.
    /// </summary>
    Task<P4ServerBuild> EnsureAsync(
        P4ServerRelease release,
        IProgress<BuildDownloadProgress>? progress,
        CancellationToken cancellationToken);

    string GetP4dPath(P4ServerBuild build);

    string GetP4Path(P4ServerBuild build);
}
