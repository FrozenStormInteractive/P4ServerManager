using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// A place the instances back up to: an encrypted backup repository on a storage. Configured once for the daemon; each
/// instance chooses its own destinations.
/// </summary>
[Index(nameof(Name), IsUnique = true)]
public sealed class BackupDestination
{
    public Guid Id { get; set; }

    [MaxLength(64)]
    public required string Name { get; set; }

    [MaxLength(32)]
    public BackupDestinationKind Kind { get; set; }

    /// <summary>
    /// Settings of the kind, without any secret, as JSON.
    /// </summary>
    public required string Settings { get; set; }

    /// <summary>
    /// Secrets of the storage, encrypted with ASP.NET Core Data Protection. Null when the storage needs none.
    /// </summary>
    public string? ProtectedCredentials { get; set; }

    /// <summary>
    /// Passphrase of the repository, encrypted with ASP.NET Core Data Protection. Never the passphrase itself.
    /// </summary>
    public required string ProtectedPassphrase { get; set; }

    /// <summary>
    /// Identifier of the repository at the destination, read from its configuration.
    /// </summary>
    public Guid RepositoryId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<InstanceBackupDestination> Instances { get; set; } = [];
}

/// <summary>
/// A destination an instance backs up to. Deleted with the instance; a destination cannot be deleted while an instance
/// backs up to it.
/// </summary>
[PrimaryKey(nameof(InstanceId), nameof(DestinationId))]
public sealed class InstanceBackupDestination
{
    public Guid InstanceId { get; set; }

    public P4ServerInstance? Instance { get; set; }

    public Guid DestinationId { get; set; }

    [DeleteBehavior(DeleteBehavior.Restrict)]
    public BackupDestination? Destination { get; set; }
}
