namespace P4ServerManager.Contracts;

/// <summary>
/// How the daemon fills the archive of p4d builds: which new builds of the Perforce FTP it downloads, how often it
/// checks, and how many builds it keeps.
/// </summary>
public sealed record BuildArchivePolicyDto
{
    /// <summary>
    /// True to check the Perforce FTP for new builds every <see cref="CheckIntervalHours"/>.
    /// </summary>
    public bool AutoDownload { get; init; }

    /// <summary>
    /// Hours between two checks, from 1 to 720.
    /// </summary>
    public int CheckIntervalHours { get; init; } = 6;

    /// <summary>
    /// Releases whose new builds are downloaded.
    /// </summary>
    public BuildArchiveScope Scope { get; init; } = BuildArchiveScope.SelectedReleases;

    /// <summary>
    /// Releases downloaded with <see cref="BuildArchiveScope.SelectedReleases"/>, for example <c>r25.1</c>. At least
    /// one with that scope and the automatic download.
    /// </summary>
    public IReadOnlyList<string> Releases { get; init; } = [];

    /// <summary>
    /// Number of builds kept per release, the newest ones, at least 1. Null to keep them all. The builds that
    /// instances use and the protected ones are always kept.
    /// </summary>
    public int? KeepLatestPerRelease { get; init; }
}

/// <summary>
/// Releases whose new builds the daemon downloads.
/// </summary>
public enum BuildArchiveScope
{
    /// <summary>
    /// Every release the Perforce FTP publishes for this platform, from the minimum release of the daemon.
    /// </summary>
    AllReleases,

    /// <summary>
    /// Only the releases of the policy.
    /// </summary>
    SelectedReleases,
}

/// <summary>
/// What the archive worker does, and how its last check went.
/// </summary>
/// <param name="Running">True while the worker runs: it checks Perforce, fetches or compresses a build.</param>
/// <param name="Activity">What it does, for example <c>Checking r25.1</c>. Null when idle.</param>
/// <param name="Percent">Progress of <paramref name="Activity"/>, when known.</param>
/// <param name="LastCheckAt">End of the last check of Perforce.</param>
/// <param name="LastCheckError">Errors of the last check, null when it succeeded.</param>
/// <param name="NextCheckAt">Next automatic check. Null when the automatic download is off.</param>
/// <param name="LastDownloadError">
/// Error of the last fetch asked for with <c>POST /api/p4d/builds/download</c>, null when it succeeded. Kept until
/// the daemon restarts.
/// </param>
/// <param name="Checking">True while the worker checks Perforce for new builds.</param>
/// <param name="Fetches">
/// The builds being fetched from Perforce, the current one first, then those waiting their turn.
/// </param>
public sealed record BuildArchiveStatusDto(
    bool Running,
    string? Activity,
    int? Percent,
    DateTimeOffset? LastCheckAt,
    string? LastCheckError,
    DateTimeOffset? NextCheckAt,
    string? LastDownloadError,
    bool Checking,
    IReadOnlyList<BuildFetchDto> Fetches);

/// <summary>
/// A build being fetched from Perforce into the archive.
/// </summary>
/// <param name="Release">Release whose current build is fetched, for example <c>r25.1</c>.</param>
/// <param name="State">Where the fetch stands.</param>
/// <param name="FileName">File being downloaded, <c>p4d</c> or <c>p4</c>, while <see cref="BuildFetchState.Downloading"/>.</param>
/// <param name="Build">
/// Version and build, for example <c>2025.1/2850123</c>, once known: while <see cref="BuildFetchState.Compressing"/>.
/// </param>
/// <param name="Percent">Progress of the state, when known.</param>
public sealed record BuildFetchDto(
    string Release,
    BuildFetchState State,
    string? FileName,
    string? Build,
    int? Percent);

/// <summary>
/// Where the fetch of a build stands.
/// </summary>
public enum BuildFetchState
{
    /// <summary>
    /// Waiting for the fetches before it.
    /// </summary>
    Queued,

    /// <summary>
    /// Reading the checksums of the release, to know whether the archive has its current build already.
    /// </summary>
    Checking,

    /// <summary>
    /// Downloading p4d, then p4.
    /// </summary>
    Downloading,

    /// <summary>
    /// Compressing the build into the archive.
    /// </summary>
    Compressing,
}
