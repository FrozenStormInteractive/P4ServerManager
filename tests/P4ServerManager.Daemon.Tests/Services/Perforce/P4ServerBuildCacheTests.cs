using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Tests.Services.Perforce;

public sealed class P4ServerBuildCacheTests : IAsyncLifetime
{
    private static readonly P4ServerPlatformDefinition PlatformDefinition = P4ServerPlatformDefinition.LinuxX64;
    private static readonly byte[] P4dContent = "fake p4d"u8.ToArray();
    private static readonly byte[] P4Content = "fake p4"u8.ToArray();

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeFtpHandler _ftp = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private P4ServerBuildCache _cache = null!;
    private TestDbContextFactory _dbContextFactory = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        _dbContextFactory = new TestDbContextFactory(_connection);
        await using (DaemonDbContext db = _dbContextFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _ftp.Files["r25.1/bin.linux26x86_64/p4d"] = P4dContent;
        _ftp.Files["r25.1/bin.linux26x86_64/p4"] = P4Content;
        _ftp.Files["r25.1/bin.linux26x86_64/SHA256SUMS"] = Encoding.UTF8.GetBytes(
            $"{Hash(P4dContent)} *p4d\n{Hash(P4Content)} *p4\n");

        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "Rev. P4D/LINUX26X86_64/2025.1/2850123 (2025/09/28).", ""));

        IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(PerforceFtpClient.HttpClientName).Returns(_ => new HttpClient(_ftp, false)
        {
            BaseAddress = new Uri("https://ftp.example.test/perforce/"),
        });

        _cache = new P4ServerBuildCache(
            new PerforceFtpClient(httpClientFactory, PlatformDefinition),
            PlatformDefinition,
            new DaemonPaths(_dataDirectory),
            _dbContextFactory,
            _processRunner,
            TimeProvider.System,
            NullLogger<P4ServerBuildCache>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureAsync_DownloadsAndPinsTheBuild()
    {
        List<BuildDownloadProgress> reports = [];

        P4ServerBuild build = await _cache.EnsureAsync(
            Release("r25.1"),
            new InlineProgress<BuildDownloadProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        build.Version.ShouldBe("2025.1");
        build.BuildNumber.ShouldBe("2850123");
        build.Sha256.ShouldBe(Hash(P4dContent));
        string expectedDirectory = Path.Combine(_dataDirectory, "builds", "linux-x64", "2025.1", "2850123");
        _cache.GetP4dPath(build).ShouldBe(Path.Combine(expectedDirectory, "p4d"));
        (await File.ReadAllBytesAsync(_cache.GetP4dPath(build), TestContext.Current.CancellationToken))
            .ShouldBe(P4dContent);
        File.Exists(_cache.GetP4Path(build)).ShouldBeTrue();
        reports.ShouldContain(r => r.FileName == "p4d" && r.Percent == 100);
        reports.ShouldContain(r => r.FileName == "p4" && r.Percent == 100);
    }

    [Fact]
    public async Task EnsureAsync_ReusesTheCachedBuildWhenTheChecksumIsUnchanged()
    {
        P4ServerBuild first = await _cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);
        int downloads = _ftp.Downloads;

        P4ServerBuild second = await _cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);

        second.Id.ShouldBe(first.Id);
        _ftp.Downloads.ShouldBe(downloads);
    }

    [Fact]
    public async Task EnsureAsync_RejectsADownloadWithAWrongChecksum()
    {
        _ftp.Files["r25.1/bin.linux26x86_64/p4d"] = "tampered"u8.ToArray();

        await Should.ThrowAsync<P4ServerBuildException>(
            () => _cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken));

        await using DaemonDbContext db = _dbContextFactory.CreateDbContext();
        (await db.Builds.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        Directory.Exists(Path.Combine(_dataDirectory, "builds", "linux-x64")).ShouldBeFalse();
    }

    [Fact]
    public async Task EnsureAsync_FailsWhenTheReleaseHasNoBuildForThePlatform()
    {
        await Should.ThrowAsync<P4ServerBuildException>(
            () => _cache.EnsureAsync(Release("r24.1"), null, TestContext.Current.CancellationToken));
    }

    private static P4ServerRelease Release(string name)
    {
        P4ServerRelease.TryParse(name, out P4ServerRelease release).ShouldBeTrue();
        return release;
    }

    private static string Hash(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DaemonDbContext>
    {
        public DaemonDbContext CreateDbContext()
        {
            return new DaemonDbContext(new DbContextOptionsBuilder<DaemonDbContext>().UseSqlite(connection).Options);
        }
    }

    private sealed class FakeFtpHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath["/perforce/".Length..];
            if (!Files.TryGetValue(path, out byte[]? content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (!path.EndsWith("SHA256SUMS", StringComparison.Ordinal))
            {
                Downloads++;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }
}
