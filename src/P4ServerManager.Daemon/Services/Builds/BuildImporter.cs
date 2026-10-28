using System.Formats.Tar;
using System.IO.Compression;

using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Builds;

/// <summary>
/// Imports p4d builds uploaded by a user: p4d and p4 as two files, or the server package of Perforce
/// (<c>helix-core-server.tgz</c>) that holds them both.
/// </summary>
public sealed class BuildImporter(P4ServerBuildCache buildCache, P4ServerPlatformDefinition platformDefinition)
{
    private const int BufferSize = 81920;

    /// <exception cref="P4ServerBuildException">The files are not a p4d and a p4 for this platform.</exception>
    /// <exception cref="P4ServerBuildConflictException">The daemon already has this build.</exception>
    public async Task<P4ServerBuild> ImportFilesAsync(Stream p4d, Stream p4, CancellationToken cancellationToken)
    {
        return await ImportAsync(async stagingDirectory =>
        {
            await CopyAsync(p4d, Path.Combine(stagingDirectory, platformDefinition.P4dFileName), cancellationToken);
            await CopyAsync(p4, Path.Combine(stagingDirectory, platformDefinition.P4FileName), cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Imports p4d and p4 from a tar archive, compressed with gzip or not. They may be in a subfolder of it.
    /// </summary>
    /// <param name="package">The archive, in a seekable stream such as an uploaded file.</param>
    /// <param name="cancellationToken">Aborts the import.</param>
    /// <exception cref="P4ServerBuildException">
    /// The package is not a tar archive, lacks p4d or p4, or they are not for this platform.
    /// </exception>
    /// <exception cref="P4ServerBuildConflictException">The daemon already has this build.</exception>
    public async Task<P4ServerBuild> ImportPackageAsync(Stream package, CancellationToken cancellationToken)
    {
        return await ImportAsync(async stagingDirectory =>
        {
            try
            {
                await ExtractPackageAsync(package, stagingDirectory, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or EndOfStreamException)
            {
                throw new P4ServerBuildException($"The package is not a tar or tgz archive: {exception.Message}");
            }

            foreach (string fileName in new[] { platformDefinition.P4dFileName, platformDefinition.P4FileName })
            {
                if (!File.Exists(Path.Combine(stagingDirectory, fileName)))
                {
                    throw new P4ServerBuildException($"The package has no {fileName}.");
                }
            }
        }, cancellationToken);
    }

    private async Task<P4ServerBuild> ImportAsync(Func<string, Task> stage, CancellationToken cancellationToken)
    {
        string stagingDirectory = buildCache.CreateStagingDirectory();
        try
        {
            await stage(stagingDirectory);
            return await buildCache.ImportAsync(stagingDirectory, cancellationToken);
        }
        finally
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private async Task ExtractPackageAsync(Stream package, string stagingDirectory, CancellationToken cancellationToken)
    {
        byte[] magic = new byte[2];
        int read = await package.ReadAtLeastAsync(magic, 2, throwOnEndOfStream: false, cancellationToken);
        package.Position = 0;
        await using GZipStream? gzip = read == 2 && magic[0] == 0x1f && magic[1] == 0x8b
            ? new GZipStream(package, CompressionMode.Decompress, leaveOpen: true)
            : null;
        Stream content = gzip ?? package;

        string[] wanted = [platformDefinition.P4dFileName, platformDefinition.P4FileName];
        await using TarReader tar = new(content, leaveOpen: true);
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            string fileName = Path.GetFileName(entry.Name);
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || !wanted.Contains(fileName)
                || entry.DataStream is null)
            {
                continue;
            }

            // Only the two known names are written: an entry can never land outside the staging directory.
            string path = Path.Combine(stagingDirectory, fileName);
            File.Delete(path);
            await CopyAsync(entry.DataStream, path, cancellationToken);
        }
    }

    private static async Task CopyAsync(Stream source, string path, CancellationToken cancellationToken)
    {
        await using FileStream destination = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true);
        await source.CopyToAsync(destination, cancellationToken);
    }
}
