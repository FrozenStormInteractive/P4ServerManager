using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Daemon.Options;

/// <summary>
/// Settings of the sign-in to the web interface, bound from the <c>Authentication</c> configuration section.
/// </summary>
public sealed class DaemonAuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// File that holds the first account, read once at startup: the user name on the first line, the password on the
    /// rest of the file. The account is created when the daemon has none, and the file is deleted in any case. Written
    /// by the install wizard of the package.
    /// </summary>
    public string? InitialUserFile { get; set; }

    /// <summary>
    /// How long a session lasts without a request. Each request renews it.
    /// </summary>
    [Range(typeof(TimeSpan), "00:05:00", "365.00:00:00")]
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Attempts to sign in, or to create the first account, accepted per minute from one IP address.
    /// </summary>
    [Range(1, 1000)]
    public int SignInAttemptsPerMinute { get; set; } = 10;
}
