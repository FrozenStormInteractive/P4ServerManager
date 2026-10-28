namespace P4ServerManager.Contracts;

/// <summary>
/// A p4d build known to the daemon, kept compressed in the archive and extracted while an instance uses it.
/// </summary>
/// <param name="Id">Identifier of the build.</param>
/// <param name="Release">Release it belongs to, for example <c>r25.1</c>.</param>
/// <param name="Version">Version reported by p4d, for example <c>2025.1</c>.</param>
/// <param name="Build">Build number reported by p4d, for example <c>2850123</c>.</param>
/// <param name="Source">Where the daemon got it from.</param>
/// <param name="Sha256">SHA-256 of the p4d executable, lower case.</param>
/// <param name="AddedAt">When it was downloaded or imported.</param>
/// <param name="ArchiveSize">Size of the compressed archive in bytes. Null while it is not archived yet.</param>
/// <param name="Extracted">True when p4d and p4 are extracted, ready to run.</param>
/// <param name="Protected">True when the retention policy never removes it.</param>
/// <param name="Instances">Instances pinned to it. A build in use cannot be removed.</param>
public sealed record BuildDto(
    Guid Id,
    string Release,
    string Version,
    string Build,
    BuildSource Source,
    string Sha256,
    DateTimeOffset AddedAt,
    long? ArchiveSize,
    bool Extracted,
    bool Protected,
    IReadOnlyList<BuildInstanceDto> Instances);

/// <param name="Id">Identifier of the instance.</param>
/// <param name="Name">Name of the instance.</param>
public sealed record BuildInstanceDto(Guid Id, string Name);

/// <summary>
/// Where the daemon got a p4d build from.
/// </summary>
public enum BuildSource
{
    /// <summary>
    /// Downloaded from the Perforce FTP.
    /// </summary>
    Ftp,

    /// <summary>
    /// Imported by a user.
    /// </summary>
    Imported,
}
