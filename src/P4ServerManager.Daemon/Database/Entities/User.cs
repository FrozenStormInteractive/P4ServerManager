using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// An account that signs in to the web interface. All the accounts have the same rights. The CLI needs none: it reaches
/// the API through the socket of the daemon.
/// </summary>
[Index(nameof(UserName), IsUnique = true)]
public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>
    /// Name the user signs in with, unique whatever its case.
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

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }
}
