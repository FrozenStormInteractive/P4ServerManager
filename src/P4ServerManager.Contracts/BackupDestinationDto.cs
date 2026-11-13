using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Contracts;

/// <summary>
/// Kind of storage a backup destination writes to.
/// </summary>
public enum BackupDestinationKind
{
    /// <summary>
    /// A folder of the machine of the daemon, a mounted network share included.
    /// </summary>
    Local,

    /// <summary>
    /// A bucket of Amazon S3.
    /// </summary>
    S3,

    /// <summary>
    /// A bucket of a storage with an S3-compatible API, such as Backblaze B2, Cloudflare R2, Wasabi or a NAS.
    /// </summary>
    S3Compatible,

    /// <summary>
    /// A bucket of Google Cloud Storage.
    /// </summary>
    GoogleCloudStorage,

    /// <summary>
    /// A container of Azure Blob Storage.
    /// </summary>
    AzureBlob,

    /// <summary>
    /// A folder of an FTP server, preferably over TLS (FTPS).
    /// </summary>
    Ftp,
}

/// <summary>
/// How an FTP destination protects the connection.
/// </summary>
public enum FtpEncryption
{
    /// <summary>
    /// TLS asked for with <c>AUTH TLS</c> on the usual port, 21 by default.
    /// </summary>
    Explicit,

    /// <summary>
    /// TLS from the first byte, on port 990 by default.
    /// </summary>
    Implicit,

    /// <summary>
    /// No encryption: the credentials and the transfers go in clear. The objects stay encrypted.
    /// </summary>
    None,
}

/// <summary>
/// Settings of a local destination.
/// </summary>
public sealed record LocalDestinationSettings
{
    /// <summary>
    /// Absolute path of the folder.
    /// </summary>
    [Required]
    public string Path { get; init; } = null!;
}

/// <summary>
/// Settings of an Amazon S3 or S3-compatible destination.
/// </summary>
public sealed record S3DestinationSettings
{
    /// <summary>
    /// The bucket.
    /// </summary>
    [Required]
    public string Bucket { get; init; } = null!;

    /// <summary>
    /// Folder of the repository in the bucket, if any, such as <c>p4/backups</c>.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// Region of the bucket, such as <c>eu-west-3</c>. Many S3-compatible storages accept any.
    /// </summary>
    [Required]
    public string Region { get; init; } = null!;

    /// <summary>
    /// URL of the S3-compatible API. Null for Amazon S3.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Whether the bucket goes in the path of the URLs rather than in the host name, as many S3-compatible storages
    /// need.
    /// </summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>
    /// Identifier of the access key. Its secret is in the credentials.
    /// </summary>
    [Required]
    public string AccessKeyId { get; init; } = null!;

    /// <summary>
    /// Storage class of the objects, such as <c>STANDARD_IA</c>. Null for the default.
    /// </summary>
    public string? StorageClass { get; init; }
}

/// <summary>
/// Settings of a Google Cloud Storage destination.
/// </summary>
public sealed record GcsDestinationSettings
{
    /// <summary>
    /// The bucket.
    /// </summary>
    [Required]
    public string Bucket { get; init; } = null!;

    /// <summary>
    /// Folder of the repository in the bucket, if any.
    /// </summary>
    public string? Prefix { get; init; }
}

/// <summary>
/// Settings of an Azure Blob Storage destination.
/// </summary>
public sealed record AzureBlobDestinationSettings
{
    /// <summary>
    /// URL of the Blob service of the account, such as <c>https://account.blob.core.windows.net</c>.
    /// </summary>
    [Required]
    public string AccountUrl { get; init; } = null!;

    /// <summary>
    /// The container.
    /// </summary>
    [Required]
    public string Container { get; init; } = null!;

    /// <summary>
    /// Folder of the repository in the container, if any.
    /// </summary>
    public string? Prefix { get; init; }
}

/// <summary>
/// Settings of an FTP destination.
/// </summary>
public sealed record FtpDestinationSettings
{
    /// <summary>
    /// Host name or address of the server.
    /// </summary>
    [Required]
    public string Host { get; init; } = null!;

    /// <summary>
    /// Port, or null for the default of <see cref="Encryption"/>: 990 for implicit TLS, 21 otherwise.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>
    /// How the connection is protected. Explicit TLS by default.
    /// </summary>
    public FtpEncryption Encryption { get; init; }

    /// <summary>
    /// Folder of the repository on the server, such as <c>/backups/p4</c>.
    /// </summary>
    [Required]
    public string Path { get; init; } = null!;

    /// <summary>
    /// User to log in as.
    /// </summary>
    [Required]
    public string UserName { get; init; } = null!;

    /// <summary>
    /// SHA-256 fingerprint of the certificate of the server, in hexadecimal, to trust a self-signed certificate. Null
    /// to validate the certificate as usual.
    /// </summary>
    public string? CertificateFingerprint { get; init; }
}

/// <summary>
/// Secrets of a destination. They are only ever sent to the daemon, which stores them encrypted and never returns them.
/// </summary>
public sealed record BackupDestinationCredentials
{
    /// <summary>
    /// Secret of the S3 access key.
    /// </summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>
    /// Key of a Google Cloud service account, as the JSON file Google gives.
    /// </summary>
    public string? ServiceAccountJson { get; init; }

    /// <summary>
    /// Key of the Azure storage account.
    /// </summary>
    public string? AccountKey { get; init; }

    /// <summary>
    /// Shared access signature of the Azure container, instead of the account key.
    /// </summary>
    public string? SasToken { get; init; }

    /// <summary>
    /// Password of the FTP user.
    /// </summary>
    public string? Password { get; init; }
}

/// <summary>
/// A place where the backups of the instances go: an encrypted backup repository on a storage. Exactly the settings of
/// its kind are set.
/// </summary>
/// <param name="Id">Identifier of the destination.</param>
/// <param name="Name">Unique name.</param>
/// <param name="Kind">Kind of storage.</param>
/// <param name="Location">Where the repository is, such as <c>s3://bucket/prefix</c>. Never a credential.</param>
/// <param name="Local">Settings of a local destination.</param>
/// <param name="S3">Settings of an S3 or S3-compatible destination.</param>
/// <param name="Gcs">Settings of a Google Cloud Storage destination.</param>
/// <param name="Azure">Settings of an Azure Blob Storage destination.</param>
/// <param name="Ftp">Settings of an FTP destination.</param>
/// <param name="HasCredentials">Whether secrets are stored for the destination.</param>
/// <param name="RepositoryId">Identifier of the backup repository at the destination.</param>
/// <param name="InstanceCount">Instances that back up to the destination.</param>
/// <param name="CreatedAt">When the destination was added.</param>
/// <param name="UpdatedAt">When it was last changed.</param>
public sealed record BackupDestinationDto(
    Guid Id,
    string Name,
    BackupDestinationKind Kind,
    string Location,
    LocalDestinationSettings? Local,
    S3DestinationSettings? S3,
    GcsDestinationSettings? Gcs,
    AzureBlobDestinationSettings? Azure,
    FtpDestinationSettings? Ftp,
    bool HasCredentials,
    Guid RepositoryId,
    int InstanceCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Outcome of a test of a destination: an object written, read back and deleted.
/// </summary>
/// <param name="Succeeded">Whether every step worked.</param>
/// <param name="Error">What failed.</param>
/// <param name="Capabilities">
/// What the storage guarantees: <c>ConditionalWrite</c>, <c>ServerSideChecksum</c>, <c>AtomicWrite</c>,
/// <c>Immutability</c>.
/// </param>
public sealed record BackupDestinationTestResult(bool Succeeded, string? Error, IReadOnlyList<string> Capabilities);
