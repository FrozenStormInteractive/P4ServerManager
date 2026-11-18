using System.Formats.Tar;
using System.Security.Cryptography;

using P4ServerManager.Daemon.Database.Entities;

using ZstdSharp;
using ZstdSharp.Unsafe;

namespace P4ServerManager.Daemon.Services.Perforce;

/// <summary>
/// Keeps each p4d build as a tar of p4d and p4, compressed with zstd at a high level, in
/// <c>archive/{platform}/{version}/{build}.tar.zst</c>. Every archive stands alone: removing one never affects another.
/// </summary>
public sealed class P4ServerBuildArchive(
    P4ServerPlatformDefinition platformDefinition,
    DaemonPaths paths,
    ILogger<P4ServerBuildArchive> logger)
{
    // Level 19 with long-distance matching is the best ratio that still decompresses with the default window limit:
    // 128 MiB, enough to cover p4d and p4 together, so that the code they share compresses once.
    private const int CompressionLevel = 19;
    private const int WindowLog = 27;

    internal const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    // Compressing at this level takes the CPU of a small NAS for a while: one build at a time.
    private readonly SemaphoreSlim compressionLock = new(1, 1);

    public string GetPath(P4ServerBuild build)
    {
        return paths.GetArchivePath(build.Platform, build.Version, build.BuildNumber);
    }

    public bool Exists(P4ServerBuild build)
    {
        return File.Exists(GetPath(build));
    }

    /// <summary>
    /// Compresses p4d and p4 of <paramref name="sourceDirectory"/> into the archive of the build, replacing any
    /// previous archive. The archive only appears once complete.
    /// </summary>
    /// <param name="build">Build to archive.</param>
    /// <param name="sourceDirectory">Directory that holds p4d and p4 of the build.</param>
    /// <param name="progress">Share of p4d and p4 compressed so far, in percent.</param>
    /// <param name="cancellationToken">Cancels the compression.</param>
    /// <returns>Size of the archive in bytes.</returns>
    public async Task<long> CreateAsync(
        P4ServerBuild build,
        string sourceDirectory,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        string archivePath = GetPath(build);
        string temporaryPath = archivePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        await compressionLock.WaitAsync(cancellationToken);
        try
        {
            logger.LogInformation("Archiving p4d {Version}/{Build}", build.Version, build.BuildNumber);
            // The compression is CPU bound: it leaves the thread pool free for the requests meanwhile.
            await Task.Run(
                () => WriteArchiveAsync(sourceDirectory, temporaryPath, progress, cancellationToken),
                cancellationToken);
            File.Move(temporaryPath, archivePath, overwrite: true);
            return new FileInfo(archivePath).Length;
        }
        finally
        {
            compressionLock.Release();
            File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Extracts p4d and p4 of the archive of the build into <paramref name="targetDirectory"/>, which must not exist,
    /// and checks them against the hashes of the build. The directory only appears once complete and checked.
    /// </summary>
    /// <param name="build">Build to extract.</param>
    /// <param name="targetDirectory">Directory to create with p4d and p4 of the build.</param>
    /// <param name="progress">Share of the archive read so far, in percent.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <exception cref="P4ServerBuildException">The archive is missing, damaged, or does not match the build.</exception>
    public async Task ExtractAsync(
        P4ServerBuild build,
        string targetDirectory,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        string archivePath = GetPath(build);
        if (!File.Exists(archivePath))
        {
            throw new P4ServerBuildException(
                $"p4d {build.Version}/{build.BuildNumber} is neither extracted nor archived.");
        }

        string temporaryDirectory = targetDirectory + ".tmp";
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            try
            {
                await ReadArchiveAsync(archivePath, temporaryDirectory, progress, cancellationToken);
            }
            catch (Exception exception) when (exception is ZstdException or InvalidDataException or FormatException)
            {
                throw new P4ServerBuildException(
                    $"The archive of p4d {build.Version}/{build.BuildNumber} is damaged: {exception.Message}");
            }

            await CheckAsync(build, platformDefinition.P4dFileName, build.Sha256, temporaryDirectory, cancellationToken);
            if (build.P4Sha256 is not null)
            {
                await CheckAsync(build, platformDefinition.P4FileName, build.P4Sha256, temporaryDirectory, cancellationToken);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
            Directory.Move(temporaryDirectory, targetDirectory);
            logger.LogInformation("Extracted p4d {Version}/{Build} from the archive", build.Version, build.BuildNumber);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    public void Delete(P4ServerBuild build)
    {
        string path = GetPath(build);
        File.Delete(path);

        // Leaves no empty folder behind for the version.
        string directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    /// <summary>
    /// Hashes a file, as written in <c>SHA256SUMS</c>: hexadecimal, lower case.
    /// </summary>
    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>
    /// Makes a file executable by everyone, as the Perforce FTP does not keep the mode.
    /// </summary>
    public static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, ExecutableMode);
        }
    }

    private async Task WriteArchiveAsync(
        string sourceDirectory,
        string archivePath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        string[] sources = [platformDefinition.P4dFileName, platformDefinition.P4FileName];
        long totalBytes = sources.Sum(fileName => new FileInfo(Path.Combine(sourceDirectory, fileName)).Length);
        long doneBytes = 0;

        await using FileStream file = new(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await using CompressionStream compression = new(file, CompressionLevel, leaveOpen: true);
        compression.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
        compression.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, WindowLog);
        compression.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);

        await using (TarWriter tar = new(compression, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (string fileName in sources)
            {
                await using FileStream input = File.OpenRead(Path.Combine(sourceDirectory, fileName));
                await using ProgressReadStream source = new(input, totalBytes, doneBytes, progress);
                PaxTarEntry entry = new(TarEntryType.RegularFile, fileName)
                {
                    Mode = ExecutableMode,
                    DataStream = source,
                };
                await tar.WriteEntryAsync(entry, cancellationToken);
                doneBytes += input.Length;
            }
        }
    }

    private async Task ReadArchiveAsync(
        string archivePath,
        string targetDirectory,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        string[] expected = [platformDefinition.P4dFileName, platformDefinition.P4FileName];

        await using FileStream file = File.OpenRead(archivePath);
        await using ProgressReadStream source = new(file, file.Length, 0, progress);
        await using DecompressionStream decompression = new(source, leaveOpen: true);
        await using TarReader tar = new(decompression, leaveOpen: true);
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            // Only the two known names are written: an entry can never land outside the directory.
            if (entry.EntryType != TarEntryType.RegularFile || !expected.Contains(entry.Name) || entry.DataStream is null)
            {
                continue;
            }

            string path = Path.Combine(targetDirectory, entry.Name);
            await using (FileStream destination = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await entry.DataStream.CopyToAsync(destination, cancellationToken);
            }
            MakeExecutable(path);
        }
    }

    private static async Task CheckAsync(
        P4ServerBuild build,
        string fileName,
        string expectedHash,
        string directory,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path) || await HashAsync(path, cancellationToken) != expectedHash)
        {
            throw new P4ServerBuildException(
                $"The archive of p4d {build.Version}/{build.BuildNumber} has no valid {fileName}.");
        }
    }
}

/// <summary>
/// Reads a stream and reports, in percent, how far the reads got through a total that may span several streams: the
/// stream starts at <c>offset</c> bytes of <c>totalBytes</c>. Each percent is reported once.
/// </summary>
internal sealed class ProgressReadStream(Stream inner, long totalBytes, long offset, IProgress<int>? progress) : Stream
{
    private int lastPercent = -1;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Report(inner.Read(buffer, offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        return Report(inner.Read(buffer));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return Report(await inner.ReadAsync(buffer, cancellationToken));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return inner.Seek(offset, origin);
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    private int Report(int read)
    {
        if (progress is not null && totalBytes > 0)
        {
            int percent = (int)Math.Min(100, (offset + inner.Position) * 100 / totalBytes);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress.Report(percent);
            }
        }
        return read;
    }
}
