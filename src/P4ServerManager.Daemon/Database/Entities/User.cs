using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// An account that signs in to the web interface. Every account has all rights on the servers; only administrators
/// manage the accounts. The CLI needs none: it reaches the API through the socket of the daemon, with the rights of an
/// administrator.
/// </summary>
[Index(nameof(UserName), IsUnique = true)]
public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>
    /// Name the user signs in with, unique. Case-sensitive: <c>admin</c> and <c>Admin</c> are two accounts.
    /// </summary>
    [MaxLength(64)]
    public required string UserName { get; set; }

    /// <summary>
    /// Salted PBKDF2 hash of the password, in the format of <c>PasswordHasher</c>. Never the password itself.
    /// </summary>
    [MaxLength(256)]
    public required string PasswordHash { get; set; }

    /// <summary>
    /// Changes with the password. The sessions signed in with another stamp are rejected.
    /// </summary>
    public Guid SecurityStamp { get; set; }

    /// <summary>
    /// Whether the user manages the accounts: creates them, sets their passwords, deletes them and names the other
    /// administrators. Once accounts exist, at least one of them is an administrator.
    /// </summary>
    public bool IsAdministrator { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }
}
