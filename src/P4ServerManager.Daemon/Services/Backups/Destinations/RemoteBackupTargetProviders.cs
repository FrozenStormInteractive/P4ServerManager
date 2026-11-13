using P4ServerManager.Backup.Azure;
using P4ServerManager.Backup.Ftp;
using P4ServerManager.Backup.Gcs;
using P4ServerManager.Backup.S3;
using P4ServerManager.Backup.Targets;
using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Backups.Destinations;

/// <summary>
/// Buckets of Amazon S3 and of S3-compatible storages.
/// </summary>
public sealed class S3BackupTargetProvider : IBackupTargetProvider
{
    public IReadOnlyCollection<BackupDestinationKind> Kinds { get; } =
        [BackupDestinationKind.S3, BackupDestinationKind.S3Compatible];

    public string GetLocation(BackupDestinationConfiguration configuration)
    {
        S3DestinationSettings settings = configuration.S3!;
        string prefix = new BackupTargetPath(settings.Prefix).Prefix;
        return configuration.Kind == BackupDestinationKind.S3 || string.IsNullOrEmpty(settings.Endpoint)
            ? $"s3://{settings.Bucket}/{prefix}"
            : $"{settings.Endpoint.TrimEnd('/')}/{settings.Bucket}/{prefix}";
    }

    public IBackupTarget Create(BackupDestinationConfiguration configuration)
    {
        S3DestinationSettings settings = configuration.S3!;
        return new S3BackupTarget(new S3TargetOptions(
            settings.Bucket,
            settings.Prefix,
            settings.Region,
            configuration.Kind == BackupDestinationKind.S3Compatible ? settings.Endpoint : null,
            settings.ForcePathStyle,
            settings.AccessKeyId,
            configuration.Credentials.SecretAccessKey ?? string.Empty,
            settings.StorageClass));
    }
}

/// <summary>
/// Buckets of Google Cloud Storage.
/// </summary>
public sealed class GcsBackupTargetProvider(DaemonPaths paths) : IBackupTargetProvider
{
    public IReadOnlyCollection<BackupDestinationKind> Kinds { get; } = [BackupDestinationKind.GoogleCloudStorage];

    public string GetLocation(BackupDestinationConfiguration configuration)
    {
        GcsDestinationSettings settings = configuration.Gcs!;
        return $"gs://{settings.Bucket}/{new BackupTargetPath(settings.Prefix).Prefix}";
    }

    public IBackupTarget Create(BackupDestinationConfiguration configuration)
    {
        GcsDestinationSettings settings = configuration.Gcs!;
        return new GcsBackupTarget(new GcsTargetOptions(
            settings.Bucket,
            settings.Prefix,
            configuration.Credentials.ServiceAccountJson,
            paths.BackupTemporaryDirectory));
    }
}

/// <summary>
/// Containers of Azure Blob Storage.
/// </summary>
public sealed class AzureBlobBackupTargetProvider : IBackupTargetProvider
{
    public IReadOnlyCollection<BackupDestinationKind> Kinds { get; } = [BackupDestinationKind.AzureBlob];

    public string GetLocation(BackupDestinationConfiguration configuration)
    {
        AzureBlobDestinationSettings settings = configuration.Azure!;
        return $"{settings.AccountUrl.TrimEnd('/')}/{settings.Container}/{new BackupTargetPath(settings.Prefix).Prefix}";
    }

    public IBackupTarget Create(BackupDestinationConfiguration configuration)
    {
        AzureBlobDestinationSettings settings = configuration.Azure!;
        return new AzureBlobBackupTarget(new AzureBlobTargetOptions(
            settings.AccountUrl,
            settings.Container,
            settings.Prefix,
            configuration.Credentials.AccountKey,
            configuration.Credentials.SasToken));
    }
}

/// <summary>
/// Folders of FTP servers, over TLS unless asked otherwise.
/// </summary>
public sealed class FtpBackupTargetProvider(DaemonPaths paths) : IBackupTargetProvider
{
    public IReadOnlyCollection<BackupDestinationKind> Kinds { get; } = [BackupDestinationKind.Ftp];

    public string GetLocation(BackupDestinationConfiguration configuration)
    {
        FtpDestinationSettings settings = configuration.Ftp!;
        string scheme = settings.Encryption == FtpEncryption.None ? "ftp" : "ftps";
        string port = settings.Port is { } value ? $":{value}" : string.Empty;
        return $"{scheme}://{settings.Host}{port}/{settings.Path.Trim('/')}";
    }

    public IBackupTarget Create(BackupDestinationConfiguration configuration)
    {
        FtpDestinationSettings settings = configuration.Ftp!;
        return new FtpBackupTarget(new FtpTargetOptions(
            settings.Host,
            settings.Port,
            settings.Encryption switch
            {
                FtpEncryption.Implicit => FtpTargetEncryption.Implicit,
                FtpEncryption.None => FtpTargetEncryption.None,
                _ => FtpTargetEncryption.Explicit,
            },
            settings.Path,
            settings.UserName,
            configuration.Credentials.Password ?? string.Empty,
            settings.CertificateFingerprint,
            paths.BackupTemporaryDirectory));
    }
}
