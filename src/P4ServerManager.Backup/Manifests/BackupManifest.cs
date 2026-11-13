using System.Text.Json.Serialization;

namespace P4ServerManager.Backup.Manifests;

/// <summary>
/// What a backup point is made of. Written last, once every object it lists is stored: a point that is listed is
/// complete. It lists every object needed to restore the point, not only the ones the backup stored.
/// </summary>
/// <param name="Header">What the point is.</param>
/// <param name="Entries">The files of the point.</param>
public sealed record BackupManifest(BackupManifestHeader Header, IReadOnlyList<BackupManifestEntry> Entries);

/// <summary>
/// What a backup point is. The repository knows nothing of what is backed up: the caller describes it with
/// <see cref="Kind"/> and <see cref="Labels"/>.
/// </summary>
public sealed class BackupManifestHeader
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required Guid Id { get; init; }

    /// <summary>
    /// What was backed up, such as an instance. The points of a source are listed together.
    /// </summary>
    public required Guid SourceId { get; init; }

    public required string SourceName { get; init; }

    /// <summary>
    /// Kind of the point, as the caller names it, such as <c>full</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The point this one builds on, if any.
    /// </summary>
    public Guid? BaseId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// What else the caller keeps about the point.
    /// </summary>
    public Dictionary<string, string> Labels { get; init; } = [];
}

/// <summary>
/// A file of a backup point.
/// </summary>
/// <param name="Role">What the file is to the caller, such as <c>checkpoint</c>.</param>
/// <param name="Path">Path of the file, relative, with <c>/</c> as separator.</param>
/// <param name="Size">Size of the content in bytes.</param>
/// <param name="ModifiedAt">Last write time of the file when it was read.</param>
/// <param name="Sha256">SHA-256 of the content, in lowercase hexadecimal.</param>
/// <param name="Object">Name of the object that holds the content.</param>
public sealed record BackupManifestEntry(
    string Role,
    string Path,
    long Size,
    DateTimeOffset ModifiedAt,
    string Sha256,
    string Object);

/// <summary>
/// A backup point listed in a repository, from the key of its manifest.
/// </summary>
/// <param name="Key">Key of the manifest.</param>
/// <param name="SourceId">What was backed up.</param>
/// <param name="CreatedAt">When the point was made.</param>
/// <param name="Id">Identifier of the point.</param>
public sealed record BackupSnapshotInfo(string Key, Guid SourceId, DateTimeOffset CreatedAt, Guid Id);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BackupManifestHeader))]
[JsonSerializable(typeof(BackupManifestEntry))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
