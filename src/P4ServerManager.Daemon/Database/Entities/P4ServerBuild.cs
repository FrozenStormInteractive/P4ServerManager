using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// An exact p4d build known to the daemon, with the matching p4 client. Every build is kept compressed in the archive;
/// the builds that instances run are also extracted in <c>builds/</c>.
/// </summary>
[Index(nameof(Platform), nameof(Version), nameof(BuildNumber), IsUnique = true)]
public sealed class P4ServerBuild
{
    public Guid Id { get; set; }

    /// <summary>
    /// Release it belongs to, for example <c>r25.1</c>.
    /// </summary>
    [MaxLength(16)]
    public required string Release { get; set; }

    /// <summary>
    /// Version reported by <c>p4d -V</c>, for example <c>2025.1</c>.
    /// </summary>
    [MaxLength(16)]
    public required string Version { get; set; }

    /// <summary>
    /// Build number reported by <c>p4d -V</c>, for example <c>2850123</c>.
    /// </summary>
    [MaxLength(16)]
    public required string BuildNumber { get; set; }

    /// <summary>
    /// Runtime platform the build runs on, for example <c>linux-x64</c>.
    /// </summary>
    [MaxLength(16)]
    public required string Platform { get; set; }

    /// <summary>
    /// SHA-256 of the p4d executable, lower case. A build of the Perforce FTP is recognized by it in
    /// <c>SHA256SUMS</c>.
    /// </summary>
    [MaxLength(64)]
    public required string Sha256 { get; set; }

    /// <summary>
    /// SHA-256 of the p4 executable, lower case. Null for a build cached before the archive existed, until it is
    /// archived.
    /// </summary>
    [MaxLength(64)]
    public string? P4Sha256 { get; set; }

    /// <summary>
    /// Whether the build was imported by a user rather than downloaded from the Perforce FTP.
    /// </summary>
    public bool Imported { get; set; }

    /// <summary>
    /// Size of the compressed archive in bytes. Null until the build is archived.
    /// </summary>
    public long? ArchiveSize { get; set; }

    /// <summary>
    /// Whether p4d and p4 are extracted in the build directory, ready to run.
    /// </summary>
    public bool IsExtracted { get; set; }

    /// <summary>
    /// Whether the retention policy leaves the build alone.
    /// </summary>
    public bool Protected { get; set; }

    /// <summary>
    /// When the daemon got the build: downloaded or imported.
    /// </summary>
    public DateTimeOffset DownloadedAt { get; set; }

    public List<P4ServerInstance> Instances { get; set; } = [];
}
