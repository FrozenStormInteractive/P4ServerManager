using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Daemon.Contracts;

/// <summary>
/// Settings of the instance that takes over the server of a legacy profile. The root comes from the profile, and the
/// Unicode mode from its database.
/// </summary>
public sealed record ImportLegacyProfileRequest
{
    /// <summary>
    /// Unique name: a letter, then up to 31 letters, digits, '-' or '_'. The server ID stored in the database is kept.
    /// </summary>
    [Required]
    public string Name { get; init; } = null!;

    /// <summary>
    /// Optional free text.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// p4d release from <c>GET /api/p4d/versions</c>, for example <c>r25.1</c>. The database is upgraded to it, and
    /// cannot go back to an older release: pick the release the server ran, or a newer one.
    /// </summary>
    [Required]
    public string Release { get; init; } = null!;

    /// <summary>
    /// TCP port p4d listens on, between 1024 and 65535. The server of the profile must not run any more.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// False to run p4d case-insensitive (<c>-C1</c>). It must match the way the server was created.
    /// </summary>
    public bool CaseSensitive { get; init; } = true;

    /// <summary>
    /// True to start p4d when the daemon starts. True when missing. It can be changed later.
    /// </summary>
    public bool AutoStart { get; init; } = true;
}
