using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Contracts;

/// <summary>
/// A release whose current build the daemon downloads from the Perforce FTP into the archive.
/// </summary>
public sealed record DownloadBuildRequest
{
    /// <summary>
    /// p4d release, for example <c>r25.1</c>. The Perforce FTP only publishes its latest patch.
    /// </summary>
    [Required]
    public string Release { get; init; } = null!;
}

/// <summary>
/// Settings of a build that can change. The settings left out are kept.
/// </summary>
public sealed record UpdateBuildRequest
{
    /// <summary>
    /// True so that the retention policy never removes the build.
    /// </summary>
    public bool? Protected { get; init; }
}
