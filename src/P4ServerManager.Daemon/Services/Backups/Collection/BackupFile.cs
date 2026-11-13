using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace P4ServerManager.Daemon.Services.Backups.Collection;

/// <summary>
/// A file of the server that goes into a backup point.
/// </summary>
/// <param name="Role">What it is, from <see cref="BackupFileRoles"/>.</param>
/// <param name="ManifestPath">Its path in the point, such as <c>depots/depot/file.txt,v</c>.</param>
/// <param name="FullPath">Its path on the disk.</param>
/// <param name="Size">Its size when it was read.</param>
/// <param name="ModifiedAt">Its last write time when it was read.</param>
/// <param name="Sha256">SHA-256 of its content, in lowercase hexadecimal.</param>
public sealed record BackupFile(
    string Role,
    string ManifestPath,
    string FullPath,
    long Size,
    DateTimeOffset ModifiedAt,
    string Sha256)
{
    /// <summary>
    /// Whether compressing the file is worth it: not for what gzip already compressed.
    /// </summary>
    public bool Compress => !FullPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What a file of a backup point is to the daemon.
/// </summary>
public static class BackupFileRoles
{
    public const string Checkpoint = "checkpoint";

    /// <summary>
    /// The MD5 p4d writes next to a checkpoint.
    /// </summary>
    public const string Checksum = "checksum";

    public const string Journal = "journal";

    /// <summary>
    /// A file of a depot.
    /// </summary>
    public const string Archive = "archive";

    /// <summary>
    /// A file of the root that the checkpoint does not hold: server ID, license, SSL key and certificate.
    /// </summary>
    public const string RootFile = "root-file";

    public static bool IsMetadata(string role)
    {
        return role is Checkpoint or Checksum or Journal;
    }
}

/// <summary>
/// Size, last write time and SHA-256 of the files of an instance at its last backup, in a file of the instance. A file
/// whose size and date did not change is not read again. The index is only an optimization: a missing or unreadable one
/// makes the next backup read every file.
/// </summary>
public sealed class BackupFileIndex
{
    private const int BufferSize = 1024 * 1024;

    private readonly Dictionary<string, IndexEntry> entries;

    private BackupFileIndex(Dictionary<string, IndexEntry> entries)
    {
        this.entries = entries;
    }

    public static async Task<BackupFileIndex> LoadAsync(string path, CancellationToken cancellationToken)
    {
        Dictionary<string, IndexEntry> entries = new(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return new BackupFileIndex(entries);
        }

        try
        {
            using StreamReader reader = new(path);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                IndexEntry? entry = JsonSerializer.Deserialize(line, BackupIndexJsonContext.Default.IndexEntry);
                if (entry is not null)
                {
                    entries[entry.Path] = entry;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            entries.Clear();
        }
        return new BackupFileIndex(entries);
    }

    /// <summary>
    /// Reads a file: from the index when its size and date are unchanged, otherwise by hashing it.
    /// </summary>
    /// <returns>The file, or null when it was deleted meanwhile.</returns>
    public async Task<BackupFile?> ReadAsync(
        string role,
        string manifestPath,
        string fullPath,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(fullPath);
        if (!info.Exists)
        {
            return null;
        }

        long size = info.Length;
        long modified = info.LastWriteTimeUtc.Ticks;
        if (entries.TryGetValue(fullPath, out IndexEntry? known) && known.Size == size && known.Modified == modified)
        {
            return new BackupFile(role, manifestPath, fullPath, size, ToDate(modified), known.Sha256);
        }

        string sha256;
        try
        {
            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        entries[fullPath] = new IndexEntry(fullPath, size, modified, sha256);
        HashedFiles++;
        return new BackupFile(role, manifestPath, fullPath, size, ToDate(modified), sha256);
    }

    /// <summary>
    /// The SHA-256 the index knows for a file of this size and last write time at <paramref name="path"/>, if any.
    /// </summary>
    public string? FindSha256(string path, long size, DateTime modifiedUtc)
    {
        return entries.TryGetValue(path, out IndexEntry? known)
            && known.Size == size
            && known.Modified == modifiedUtc.Ticks
                ? known.Sha256
                : null;
    }

    /// <summary>
    /// Files read again since the index was loaded.
    /// </summary>
    public int HashedFiles { get; private set; }

    /// <summary>
    /// Saves the entries of <paramref name="files"/> only: the files no longer backed up are forgotten.
    /// </summary>
    public async Task SaveAsync(string path, IEnumerable<BackupFile> files, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp";
        await using (StreamWriter writer = new(temporaryPath))
        {
            foreach (BackupFile file in files)
            {
                IndexEntry entry = new(file.FullPath, file.Size, file.ModifiedAt.UtcTicks, file.Sha256);
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(entry, BackupIndexJsonContext.Default.IndexEntry).AsMemory(),
                    cancellationToken);
            }
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static DateTimeOffset ToDate(long ticks)
    {
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    internal sealed record IndexEntry(string Path, long Size, long Modified, string Sha256);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupFileIndex.IndexEntry))]
internal sealed partial class BackupIndexJsonContext : JsonSerializerContext;
