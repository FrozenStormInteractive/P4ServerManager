using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Contracts;

/// <summary>
/// The p4d release, or exact build, an instance moves to.
/// </summary>
public sealed record UpgradeInstanceRequest
{
    /// <summary>
    /// p4d release from <c>GET /api/p4d/versions</c>, for example <c>r25.2</c>: the release of the instance, for its
    /// latest patch, or a newer one. The database is upgraded to it and cannot go back to an older release.
    /// </summary>
    [Required]
    public string Release { get; init; } = null!;

    /// <summary>
    /// Exact build of <see cref="Release"/> from the archive, for example <c>2850123</c>, no older than the build of
    /// the instance. Defaults to the latest build of the release.
    /// </summary>
    public string? Build { get; init; }
}
