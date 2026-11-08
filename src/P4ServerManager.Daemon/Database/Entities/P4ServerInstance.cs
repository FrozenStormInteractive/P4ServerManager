using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// A p4d server managed by the daemon.
/// </summary>
[Index(nameof(Name), IsUnique = true)]
[Index(nameof(Port), IsUnique = true)]
public sealed class P4ServerInstance
{
    public Guid Id { get; set; }

    [MaxLength(32)]
    public required string Name { get; set; }

    [MaxLength(512)]
    public string? Description { get; set; }

    public int Port { get; set; }

    /// <summary>
    /// Whether p4d listens with SSL. Its key and certificate are in the <c>ssl</c> folder of the root.
    /// </summary>
    public bool Ssl { get; set; }

    /// <summary>
    /// Fingerprint of the certificate of the server, read once it is generated. Null without SSL.
    /// </summary>
    [MaxLength(128)]
    public string? SslFingerprint { get; set; }

    /// <summary>
    /// Absolute path of the P4ROOT.
    /// </summary>
    public required string RootPath { get; set; }

    /// <summary>
    /// Release asked for at creation, for example <c>r25.1</c>.
    /// </summary>
    [MaxLength(16)]
    public required string Release { get; set; }

    /// <summary>
    /// Exact p4d build the instance is pinned to. Null until the provisioning has resolved it.
    /// </summary>
    public Guid? BuildId { get; set; }

    public P4ServerBuild? Build { get; set; }

    public bool CaseSensitive { get; set; }

    public bool Unicode { get; set; }

    /// <summary>
    /// Whether the daemon starts p4d when it starts, if p4d does not already run.
    /// </summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// Whether p4d runs in maintenance mode (<c>p4d -n</c>): it only allows the commands that do not need a client
    /// workspace. Every start of p4d follows it, until its maintenance mode is disabled.
    /// </summary>
    public bool MaintenanceMode { get; set; }

    public P4ServerInstanceState State { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
