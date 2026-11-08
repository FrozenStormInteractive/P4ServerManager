using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Daemon.Options;

/// <summary>
/// Settings of the daemon, bound from the <c>Daemon</c> configuration section.
/// </summary>
public sealed class DaemonOptions
{
    public const string SectionName = "Daemon";

    public const string DefaultSocketPath = "daemon.sock";

    /// <summary>
    /// Folder that holds the database, the cached p4d builds and the instances. A relative path is resolved against
    /// the content root.
    /// </summary>
    [Required]
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Folder of the compressed p4d builds, to keep the archive on another volume. A relative path is resolved against
    /// the data directory. Defaults to the <c>archive</c> folder of the data directory.
    /// </summary>
    public string? ArchiveDirectory { get; set; }

    /// <summary>
    /// Unix domain socket the CLI reaches the API through, next to the TCP port of the web UI. A relative path is
    /// resolved against the data directory. Empty to listen on no socket.
    /// </summary>
    public string? SocketPath { get; set; } = DefaultSocketPath;

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

    /// <summary>
    /// Interval between two checks of the stored super user account of each running instance. An account found
    /// invalid is not checked again until it is changed or checked on demand.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan SuperUserCheckInterval { get; set; } = TimeSpan.FromHours(1);
}
