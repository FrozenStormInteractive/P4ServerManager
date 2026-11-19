using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Amazon.Runtime;
using Amazon.S3;

using Azure.Storage;
using Azure.Storage.Blobs;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

using Google.Cloud.Storage.V1;

using P4ServerManager.Backup.Azure;
using P4ServerManager.Backup.Ftp;
using P4ServerManager.Backup.Gcs;
using P4ServerManager.Backup.S3;
using P4ServerManager.Backup.Targets;

namespace P4ServerManager.Backup.Tests.Targets;

/// <summary>
/// An emulator of a storage in a container, started once for the tests of a class. Without Docker, it stays stopped and
/// the tests are skipped.
/// </summary>
public abstract class StorageContainerFixture : IAsyncLifetime
{
    private IContainer? _container;

    /// <summary>
    /// Whether the emulator runs.
    /// </summary>
    public bool Available => _container is not null;

    /// <summary>
    /// Why the emulator does not run.
    /// </summary>
    public string? Error { get; private set; }

    protected IContainer Container => _container ?? throw new InvalidOperationException("The container is not running.");

    public async ValueTask InitializeAsync()
    {
        try
        {
            IContainer container = Build();
            await container.StartAsync();
            _container = container;
            await PrepareAsync();
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            if (_container is not null)
            {
                await _container.DisposeAsync();
                _container = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }

    protected abstract IContainer Build();

    /// <summary>
    /// Creates what the tests need once the emulator runs, such as the bucket.
    /// </summary>
    protected virtual Task PrepareAsync()
    {
        return Task.CompletedTask;
    }

    protected static int GetFreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Retries an action while the emulator finishes starting.
    /// </summary>
    protected static async Task RetryAsync(Func<Task> action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }
}

/// <summary>
/// The S3 gateway of SeaweedFS, in place of MinIO, which is no longer maintained.
/// </summary>
public sealed class SeaweedFsFixture : StorageContainerFixture
{
    public const string Bucket = "backups";

    public string ServiceUrl => $"http://{Container.Hostname}:{Container.GetMappedPublicPort(8333)}";

    protected override IContainer Build()
    {
        // SeaweedFS refuses signed requests until it knows the keys.
        const string identities = """
            {"identities":[{"name":"tests","credentials":[{"accessKey":"any","secretKey":"any"}],
            "actions":["Admin","Read","Write","List","Tagging"]}]}
            """;
        return new ContainerBuilder("chrislusf/seaweedfs:4.48")
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(identities), FilePath.Of("/etc/seaweedfs/s3.json"))
            .WithCommand("server", "-s3", "-s3.config=/etc/seaweedfs/s3.json", "-dir=/data", "-ip.bind=0.0.0.0")
            .WithPortBinding(8333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8333))
            .Build();
    }

    protected override Task PrepareAsync()
    {
        return RetryAsync(async () =>
        {
            using AmazonS3Client client = CreateClient();
            await client.PutBucketAsync(Bucket);
        });
    }

    private AmazonS3Client CreateClient()
    {
        return new AmazonS3Client(
            new BasicAWSCredentials("any", "any"),
            new AmazonS3Config
            {
                ServiceURL = ServiceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
    }
}

public sealed class SeaweedFsS3BackupTargetTests(SeaweedFsFixture fixture)
    : BackupTargetContractTests, IClassFixture<SeaweedFsFixture>
{
    protected override Task<IBackupTarget?> CreateTargetAsync()
    {
        if (!fixture.Available)
        {
            return Task.FromResult<IBackupTarget?>(null);
        }
        return Task.FromResult<IBackupTarget?>(new S3BackupTarget(new S3TargetOptions(
            SeaweedFsFixture.Bucket,
            $"tests/{Guid.NewGuid():N}",
            "us-east-1",
            fixture.ServiceUrl,
            ForcePathStyle: true,
            "any",
            "any",
            null)));
    }
}

/// <summary>
/// Azurite, the emulator of Azure Storage, with its well-known development account.
/// </summary>
public sealed class AzuriteFixture : StorageContainerFixture
{
    public const string Container_ = "backups";

    public const string AccountName = "devstoreaccount1";

    public const string AccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    public string AccountUrl => $"http://127.0.0.1:{Container.GetMappedPublicPort(10000)}/{AccountName}";

    protected override IContainer Build()
    {
        return new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite:3.35.0")
            .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck", "--loose")
            .WithPortBinding(10000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(10000))
            .Build();
    }

    protected override Task PrepareAsync()
    {
        return RetryAsync(async () =>
        {
            BlobContainerClient container = new(
                new Uri($"{AccountUrl}/{Container_}"),
                new StorageSharedKeyCredential(AccountName, AccountKey));
            await container.CreateIfNotExistsAsync();
        });
    }
}

public sealed class AzuriteBackupTargetTests(AzuriteFixture fixture)
    : BackupTargetContractTests, IClassFixture<AzuriteFixture>
{
    protected override Task<IBackupTarget?> CreateTargetAsync()
    {
        if (!fixture.Available)
        {
            return Task.FromResult<IBackupTarget?>(null);
        }
        return Task.FromResult<IBackupTarget?>(new AzureBlobBackupTarget(new AzureBlobTargetOptions(
            fixture.AccountUrl,
            AzuriteFixture.Container_,
            $"tests/{Guid.NewGuid():N}",
            AzuriteFixture.AccountKey,
            null)));
    }
}

/// <summary>
/// fake-gcs-server, an emulator of the JSON API of Google Cloud Storage. Its resumable uploads point back to its public
/// URL: the port is the same inside and outside the container.
/// </summary>
public sealed class FakeGcsFixture : StorageContainerFixture
{
    public const string Bucket = "backups";

    private readonly int _port = GetFreePort();

    public string BaseUri => $"http://localhost:{_port}/storage/v1/";

    protected override IContainer Build()
    {
        return new ContainerBuilder("fsouza/fake-gcs-server:1.56.1")
            .WithCommand(
                "-scheme", "http",
                "-port", _port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-external-url", $"http://localhost:{_port}",
                "-public-host", $"localhost:{_port}")
            .WithPortBinding(_port, _port)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(_port))
            .Build();
    }

    protected override Task PrepareAsync()
    {
        return RetryAsync(async () =>
        {
            using StorageClient client = new StorageClientBuilder { BaseUri = BaseUri, UnauthenticatedAccess = true }.Build();
            await client.CreateBucketAsync("test-project", Bucket);
        });
    }
}

public sealed class FakeGcsBackupTargetTests(FakeGcsFixture fixture)
    : BackupTargetContractTests, IClassFixture<FakeGcsFixture>
{
    protected override Task<IBackupTarget?> CreateTargetAsync()
    {
        if (!fixture.Available)
        {
            return Task.FromResult<IBackupTarget?>(null);
        }
        return Task.FromResult<IBackupTarget?>(new GcsBackupTarget(new GcsTargetOptions(
            FakeGcsFixture.Bucket,
            $"tests/{Guid.NewGuid():N}",
            null,
            Path.Combine(Path.GetTempPath(), "p4sm-backup-tests", "gcs"),
            fixture.BaseUri)));
    }
}

/// <summary>
/// Pure-FTPd over explicit TLS, with a self-signed certificate it generates when it starts: the tests trust it by its
/// fingerprint, as a user does with a NAS. The passive ports are the same inside and outside the container.
/// </summary>
public sealed class PureFtpdFixture : StorageContainerFixture
{
    public const string UserName = "backup";

    public const string Password = "backup-password";

    private const int FirstPassivePort = 30100;
    private const int PassivePortCount = 10;

    public int Port => Container.GetMappedPublicPort(21);

    public string? CertificateFingerprint { get; private set; }

    protected override IContainer Build()
    {
        ContainerBuilder builder = new ContainerBuilder("stilliard/pure-ftpd:bullseye-1.0.49")
            .WithEnvironment("PUBLICHOST", "127.0.0.1")
            .WithEnvironment("FTP_USER_NAME", UserName)
            .WithEnvironment("FTP_USER_PASS", Password)
            .WithEnvironment("FTP_USER_HOME", "/home/backup")
            .WithEnvironment("FTP_PASSIVE_PORTS", $"{FirstPassivePort}:{FirstPassivePort + PassivePortCount - 1}")
            .WithEnvironment("ADDED_FLAGS", "--tls=2")
            .WithEnvironment("TLS_CN", "localhost")
            .WithEnvironment("TLS_ORG", "P4ServerManager tests")
            .WithEnvironment("TLS_C", "FR")
            .WithPortBinding(21, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(21));
        for (int port = FirstPassivePort; port < FirstPassivePort + PassivePortCount; port++)
        {
            builder = builder.WithPortBinding(port, port);
        }
        return builder.Build();
    }

    protected override async Task PrepareAsync()
    {
        ExecResult result = await Container.ExecAsync(["cat", "/etc/ssl/private/pure-ftpd.pem"]);
        int start = result.Stdout.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal);
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(result.Stdout.AsSpan(start));
        CertificateFingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }
}

public sealed class PureFtpdBackupTargetTests(PureFtpdFixture fixture)
    : BackupTargetContractTests, IClassFixture<PureFtpdFixture>
{
    protected override int ManyObjectsCount => 60;

    protected override Task<IBackupTarget?> CreateTargetAsync()
    {
        if (!fixture.Available)
        {
            return Task.FromResult<IBackupTarget?>(null);
        }
        return Task.FromResult<IBackupTarget?>(CreateTarget(fixture.CertificateFingerprint));
    }

    [Fact]
    public async Task Connect_RefusesACertificateWithAnotherFingerprint()
    {
        SkipWithoutTarget();
        await using FtpBackupTarget target = CreateTarget(new string('0', 64));

        await Should.ThrowAsync<BackupTargetException>(() => target.ExistsAsync("object", Token));
    }

    private FtpBackupTarget CreateTarget(string? fingerprint)
    {
        return new FtpBackupTarget(new FtpTargetOptions(
            "127.0.0.1",
            fixture.Port,
            FtpTargetEncryption.Explicit,
            $"tests/{Guid.NewGuid():N}",
            PureFtpdFixture.UserName,
            PureFtpdFixture.Password,
            fingerprint,
            Path.Combine(Path.GetTempPath(), "p4sm-backup-tests", "ftp")));
    }
}
