using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Contracts;

/// <summary>
/// Adds a backup destination. A storage that holds no repository gets a new one, encrypted with
/// <see cref="Passphrase"/>; a storage that already holds one is attached if the passphrase opens it. Exactly the
/// settings of <see cref="Kind"/> are set.
/// </summary>
public sealed record CreateBackupDestinationRequest
{
    /// <summary>
    /// Unique name, up to 64 letters, digits, spaces, dots, dashes and underscores.
    /// </summary>
    [Required]
    public string Name { get; init; } = null!;

    /// <summary>
    /// Kind of storage.
    /// </summary>
    public BackupDestinationKind Kind { get; init; }

    /// <summary>
    /// Settings of a local destination.
    /// </summary>
    public LocalDestinationSettings? Local { get; init; }

    /// <summary>
    /// Settings of an S3 or S3-compatible destination.
    /// </summary>
    public S3DestinationSettings? S3 { get; init; }

    /// <summary>
    /// Settings of a Google Cloud Storage destination.
    /// </summary>
    public GcsDestinationSettings? Gcs { get; init; }

    /// <summary>
    /// Settings of an Azure Blob Storage destination.
    /// </summary>
    public AzureBlobDestinationSettings? Azure { get; init; }

    /// <summary>
    /// Settings of an FTP destination.
    /// </summary>
    public FtpDestinationSettings? Ftp { get; init; }

    /// <summary>
    /// Secrets of the storage, as its kind needs them.
    /// </summary>
    public BackupDestinationCredentials? Credentials { get; init; }

    /// <summary>
    /// Passphrase of the repository, at least 12 characters. It is the only way to read the backups from another
    /// daemon: keep it somewhere else.
    /// </summary>
    [Required]
    public string Passphrase { get; init; } = null!;
}

/// <summary>
/// Changes a backup destination. Its kind and its passphrase stay: exactly the settings of its kind are set. Moved to a
/// storage that holds no repository, it gets a new one with the same passphrase.
/// </summary>
public sealed record UpdateBackupDestinationRequest
{
    /// <summary>
    /// Unique name.
    /// </summary>
    [Required]
    public string Name { get; init; } = null!;

    /// <summary>
    /// Settings of a local destination.
    /// </summary>
    public LocalDestinationSettings? Local { get; init; }

    /// <summary>
    /// Settings of an S3 or S3-compatible destination.
    /// </summary>
    public S3DestinationSettings? S3 { get; init; }

    /// <summary>
    /// Settings of a Google Cloud Storage destination.
    /// </summary>
    public GcsDestinationSettings? Gcs { get; init; }

    /// <summary>
    /// Settings of an Azure Blob Storage destination.
    /// </summary>
    public AzureBlobDestinationSettings? Azure { get; init; }

    /// <summary>
    /// Settings of an FTP destination.
    /// </summary>
    public FtpDestinationSettings? Ftp { get; init; }

    /// <summary>
    /// New secrets, or null to keep the stored ones.
    /// </summary>
    public BackupDestinationCredentials? Credentials { get; init; }
}

/// <summary>
/// The destinations an instance backs up to.
/// </summary>
public sealed record InstanceBackupDestinations
{
    /// <summary>
    /// Identifiers of the destinations.
    /// </summary>
    [Required]
    public IReadOnlyList<Guid> DestinationIds { get; init; } = [];
}

/// <summary>
/// Begins a backup of an instance.
/// </summary>
public sealed record StartBackupRequest
{
    /// <summary>
    /// A checkpoint, or a rotation of the journal on top of the latest backup at each destination.
    /// </summary>
    public BackupKind Kind { get; init; }

    /// <summary>
    /// Destinations to back up to, among the ones of the instance. Null or empty for all of them.
    /// </summary>
    public IReadOnlyList<Guid>? DestinationIds { get; init; }
}

/// <summary>
/// Checks a backup point at one of its destinations.
/// </summary>
public sealed record VerifyBackupRequest
{
    /// <summary>
    /// The destination.
    /// </summary>
    public Guid DestinationId { get; init; }

    /// <summary>
    /// Whether to read every object back, decrypt it and compare its SHA-256, rather than only check that it exists. A
    /// deep verification downloads the whole point.
    /// </summary>
    public bool Deep { get; init; }
}
