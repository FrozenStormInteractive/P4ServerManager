using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Daemon.Options;

/// <summary>
/// Settings of the daemon, bound from the <c>Daemon</c> configuration section.
/// </summary>
public sealed class DaemonOptions
{
    public const string SectionName = "Daemon";

    /// <summary>
    /// Folder that holds the database, the cached p4d builds and the instances. A relative path is resolved against
    /// the content root.
    /// </summary>
    [Required]
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Root of the Perforce FTP server that publishes the p4d releases.
    /// </summary>
    [Required]
    [Url]
    public string PerforceFtpBaseUrl { get; set; } = "https://ftp.perforce.com/perforce/";

    /// <summary>
    /// Oldest p4d release offered when creating an instance, for example <c>r20.1</c>.
    /// </summary>
    [Required]
    [RegularExpression(@"^r\d{2}\.\d$")]
    public string MinimumRelease { get; set; } = "r20.1";
}
