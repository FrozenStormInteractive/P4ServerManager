using System.ComponentModel.DataAnnotations;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// Super user account the daemon stores for an instance, to administer its server. Owned by the instance, in a table of
/// its own: it is loaded with the instance and deleted with it.
/// </summary>
public sealed class InstanceSuperUser
{
    [MaxLength(64)]
    public required string UserName { get; set; }

    /// <summary>
    /// The password, encrypted with ASP.NET Core Data Protection. Never the password itself.
    /// </summary>
    public required string ProtectedPassword { get; set; }

    public SuperUserStatus Status { get; set; }

    /// <summary>
    /// Date of the last check that reached the server.
    /// </summary>
    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>
    /// Message of p4 when the last check failed.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Date the account was stored.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
