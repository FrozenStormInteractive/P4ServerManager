using P4ServerManager.Backup.Azure;
using P4ServerManager.Backup.Gcs;
using P4ServerManager.Backup.S3;
using P4ServerManager.Backup.Targets;

namespace P4ServerManager.Backup.Tests.Targets;

/// <summary>
/// The contract against real storages, for what no emulator guarantees: conditional writes, checksums checked by the
/// storage. Each runs only when its environment variables are set, such as in a job of the CI that holds the secrets,
/// and works under a random prefix it empties at the end.
/// </summary>
internal static class ProviderEnvironment
{
    public static string? Get(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string NewPrefix()
    {
        return $"p4sm-tests/{Guid.NewGuid():N}";
    }
}

/// <summary>
/// Amazon S3: <c>P4SM_TEST_S3_BUCKET</c>, <c>P4SM_TEST_S3_REGION</c>, <c>P4SM_TEST_S3_ACCESS_KEY_ID</c>,
/// <c>P4SM_TEST_S3_SECRET_ACCESS_KEY</c>.
/// </summary>
public sealed class AmazonS3BackupTargetTests : BackupTargetContractTests
{
    protected override Task<IBackupTarget?> CreateTargetAsync()
    {
        string? bucket = ProviderEnvironment.Get("P4SM_TEST_S3_BUCKET");
        string? region = ProviderEnvironment.Get("P4SM_TEST_S3_REGION");
        string? keyId = ProviderEnvironment.Get("P4SM_TEST_S3_ACCESS_KEY_ID");
        string? secret = ProviderEnvironment.Get("P4SM_TEST_S3_SECRET_ACCESS_KEY");
        return Task.FromResult<IBackupTarget?>(bucket is null || region is null || keyId is null || secret is null
            ? null
            : new S3BackupTarget(new S3TargetOptions(bucket, ProviderEnvironment.NewPrefix(), region, null, false, keyId, secret, null)));
    }
}

/// <summary>
/// An S3-compatible storage, such as Cloudflare R2 or Backblaze B2: <c>P4SM_TEST_S3C_ENDPOINT</c>,
/// <c>P4SM_TEST_S3C_BUCKET</c>, <c>P4SM_TEST_S3C_REGION</c>, <c>P4SM_TEST_S3C_ACCESS_KEY_ID</c>,
/// <c>P4SM_TEST_S3C_SECRET_ACCESS_KEY</c>.
/// </summary>
public sealed class S3CompatibleBackupTargetTests : BackupTargetContractTests
{
    protected override Task<IBackupTarget?> CreateTargetAsync()
    {
        string? endpoint = ProviderEnvironment.Get("P4SM_TEST_S3C_ENDPOINT");
        string? bucket = ProviderEnvironment.Get("P4SM_TEST_S3C_BUCKET");
        string? keyId = ProviderEnvironment.Get("P4SM_TEST_S3C_ACCESS_KEY_ID");
        string? secret = ProviderEnvironment.Get("P4SM_TEST_S3C_SECRET_ACCESS_KEY");
        string region = ProviderEnvironment.Get("P4SM_TEST_S3C_REGION") ?? "auto";
        return Task.FromResult<IBackupTarget?>(endpoint is null || bucket is null || keyId is null || secret is null
            ? null
            : new S3BackupTarget(new S3TargetOptions(bucket, ProviderEnvironment.NewPrefix(), region, endpoint, true, keyId, secret, null)));
    }
}

/// <summary>
/// Azure Blob Storage: <c>P4SM_TEST_AZURE_ACCOUNT_URL</c>, <c>P4SM_TEST_AZURE_CONTAINER</c>,
/// <c>P4SM_TEST_AZURE_ACCOUNT_KEY</c>.
/// </summary>
public sealed class AzureBlobProviderTests : BackupTargetContractTests
{
    protected override Task<IBackupTarget?> CreateTargetAsync()
    {
        string? accountUrl = ProviderEnvironment.Get("P4SM_TEST_AZURE_ACCOUNT_URL");
        string? container = ProviderEnvironment.Get("P4SM_TEST_AZURE_CONTAINER");
        string? key = ProviderEnvironment.Get("P4SM_TEST_AZURE_ACCOUNT_KEY");
        return Task.FromResult<IBackupTarget?>(accountUrl is null || container is null || key is null
            ? null
            : new AzureBlobBackupTarget(new AzureBlobTargetOptions(accountUrl, container, ProviderEnvironment.NewPrefix(), key, null)));
    }
}

/// <summary>
/// Google Cloud Storage: <c>P4SM_TEST_GCS_BUCKET</c>, and <c>P4SM_TEST_GCS_SERVICE_ACCOUNT_FILE</c>, the path of the
/// key of a service account.
/// </summary>
public sealed class GcsProviderTests : BackupTargetContractTests
{
    protected override async Task<IBackupTarget?> CreateTargetAsync()
    {
        string? bucket = ProviderEnvironment.Get("P4SM_TEST_GCS_BUCKET");
        string? keyFile = ProviderEnvironment.Get("P4SM_TEST_GCS_SERVICE_ACCOUNT_FILE");
        if (bucket is null || keyFile is null)
        {
            return null;
        }
        return new GcsBackupTarget(new GcsTargetOptions(
            bucket,
            ProviderEnvironment.NewPrefix(),
            await File.ReadAllTextAsync(keyFile),
            Path.Combine(Path.GetTempPath(), "p4sm-backup-tests", "gcs")));
    }
}
