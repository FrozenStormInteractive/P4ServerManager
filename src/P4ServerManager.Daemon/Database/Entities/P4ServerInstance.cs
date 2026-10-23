using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

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

    public P4ServerInstanceState State { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Lifecycle state of an instance. Whether p4d runs is not stored: the supervisor reads it from the pid file.
/// </summary>
public enum P4ServerInstanceState
{
    Provisioning,
    Ready,
    Failed,
}
