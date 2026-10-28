namespace P4ServerManager.Contracts;

/// <summary>
/// A p4d release that can be installed on this platform: published on the Perforce FTP, or kept in the archive.
/// </summary>
/// <param name="Release">Release name on the Perforce FTP, for example <c>r25.1</c>.</param>
/// <param name="Version">Version, for example <c>2025.1</c>.</param>
/// <param name="CachedBuilds">Builds of this release known to the daemon, newest first.</param>
public sealed record P4ServerVersionDto(string Release, string Version, IReadOnlyList<CachedBuildDto> CachedBuilds);

/// <param name="Build">Build number, for example <c>2850123</c>.</param>
/// <param name="DownloadedAt">When it was downloaded or imported.</param>
/// <param name="Extracted">True when it is extracted, ready to run, rather than only archived.</param>
public sealed record CachedBuildDto(string Build, DateTimeOffset DownloadedAt, bool Extracted);
