namespace P4ServerManager.Contracts;

/// <summary>
/// A p4d release that can be installed on this platform.
/// </summary>
/// <param name="Release">Release name on the Perforce FTP, for example <c>r25.1</c>.</param>
/// <param name="Version">Version, for example <c>2025.1</c>.</param>
/// <param name="CachedBuilds">Builds of this release already in the build cache.</param>
public sealed record P4ServerVersionDto(string Release, string Version, IReadOnlyList<CachedBuildDto> CachedBuilds);

/// <param name="Build">Build number, for example <c>2850123</c>.</param>
/// <param name="DownloadedAt">Download date.</param>
public sealed record CachedBuildDto(string Build, DateTimeOffset DownloadedAt);
