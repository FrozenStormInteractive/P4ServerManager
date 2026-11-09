using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// How the daemon fills the archive of p4d builds. A single row, with <see cref="SingletonId"/>; the defaults apply
/// until it is saved once.
/// </summary>
public sealed class BuildArchivePolicy
{
    public const int SingletonId = 1;

    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// Whether the daemon checks the Perforce FTP for new builds every <see cref="CheckIntervalHours"/>.
    /// </summary>
    public bool AutoDownload { get; set; }

    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>
    /// Releases whose new builds are archived.
    /// </summary>
    [MaxLength(16)]
    public BuildArchiveScope Scope { get; set; } = BuildArchiveScope.SelectedReleases;

    /// <summary>
    /// Releases archived with <see cref="BuildArchiveScope.SelectedReleases"/>, for example <c>r25.1</c>.
    /// </summary>
    public List<string> Releases { get; set; } = [];

    /// <summary>
    /// Number of builds kept per release, the newest ones. Null to keep them all. The builds that instances use and
    /// the protected ones are always kept.
    /// </summary>
    public int? KeepLatestPerRelease { get; set; }

    /// <summary>
    /// End of the last check of the Perforce FTP.
    /// </summary>
    public DateTimeOffset? LastCheckAt { get; set; }

    /// <summary>
    /// Errors of the last check, null when it succeeded.
    /// </summary>
    public string? LastCheckError { get; set; }
}
