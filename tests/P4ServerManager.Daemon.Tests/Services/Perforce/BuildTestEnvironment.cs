using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Hubs;
using P4ServerManager.Daemon.Options;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Builds;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Tests.Services.Perforce;

/// <summary>
/// A build cache on a temporary data directory, an in-memory database and a fake Perforce FTP. The fake p4d is a text
/// file, <c>p4d 2025.1/2850123</c>, whose version the fake <c>p4d -V</c> reads back.
/// </summary>
internal sealed class BuildTestEnvironment : IAsyncDisposable
{
    public static readonly P4ServerPlatformDefinition PlatformDefinition = P4ServerPlatformDefinition.LinuxX64;

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private BuildTestEnvironment()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
        Paths = new DaemonPaths(DataDirectory);
        DbContextFactory = new TestDbContextFactory(_connection);

        ProcessRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => RunFakeP4d(call.Arg<ProcessCommand>()));

        IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(PerforceFtpClient.HttpClientName).Returns(_ => new HttpClient(Ftp, false)
        {
            BaseAddress = new Uri("https://ftp.example.test/perforce/"),
        });
        FtpClient = new PerforceFtpClient(httpClientFactory, PlatformDefinition);

        IHubContext<BuildsHub, IBuildsClient> hubContext = Substitute.For<IHubContext<BuildsHub, IBuildsClient>>();
        hubContext.Clients.All.Returns(BuildsClient);
        Notifier = new BuildNotifier(hubContext, DbContextFactory, NullLogger<BuildNotifier>.Instance);

        Archive = new P4ServerBuildArchive(PlatformDefinition, Paths, NullLogger<P4ServerBuildArchive>.Instance);
        Cache = new P4ServerBuildCache(
            FtpClient,
            Archive,
            PlatformDefinition,
            Paths,
            DbContextFactory,
            ProcessRunner,
            OperationTracker,
            Notifier,
            TimeProvider.System,
            NullLogger<P4ServerBuildCache>.Instance);
        Synchronizer = new BuildArchiveSynchronizer(
            Cache,
            FtpClient,
            DbContextFactory,
            PlatformDefinition,
            Microsoft.Extensions.Options.Options.Create(new DaemonOptions { MinimumRelease = "r24.1" }),
            NullLogger<BuildArchiveSynchronizer>.Instance);
    }

    public string DataDirectory { get; }

    public DaemonPaths Paths { get; }

    public IDbContextFactory<DaemonDbContext> DbContextFactory { get; }

    public FakeFtpHandler Ftp { get; } = new();

    public IProcessRunner ProcessRunner { get; } = Substitute.For<IProcessRunner>();

    public IBuildsClient BuildsClient { get; } = Substitute.For<IBuildsClient>();

    public OperationTracker OperationTracker { get; } = new();

    public PerforceFtpClient FtpClient { get; }

    public BuildNotifier Notifier { get; }

    public P4ServerBuildArchive Archive { get; }

    public P4ServerBuildCache Cache { get; }

    public BuildArchiveSynchronizer Synchronizer { get; }

    public static async Task<BuildTestEnvironment> CreateAsync()
    {
        BuildTestEnvironment environment = new();
        await environment._connection.OpenAsync();
        await using DaemonDbContext db = environment.DbContextFactory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return environment;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }

    public static byte[] P4dContent(string versionAndBuild)
    {
        return Encoding.UTF8.GetBytes($"p4d {versionAndBuild}");
    }

    public static byte[] P4Content(string versionAndBuild)
    {
        return Encoding.UTF8.GetBytes($"p4 {versionAndBuild}");
    }

    /// <summary>
    /// Publishes a build as the current patch of its release on the fake FTP, with its <c>SHA256SUMS</c>.
    /// </summary>
    public void Publish(string release, string versionAndBuild)
    {
        Publish(release, P4dContent(versionAndBuild), P4Content(versionAndBuild));
    }

    public void Publish(string release, byte[] p4d, byte[] p4)
    {
        string folder = $"{release}/{PlatformDefinition.FtpDirectory}";
        Ftp.Files[$"{folder}/p4d"] = p4d;
        Ftp.Files[$"{folder}/p4"] = p4;
        Ftp.Files[$"{folder}/SHA256SUMS"] = Encoding.UTF8.GetBytes($"{Hash(p4d)} *p4d\n{Hash(p4)} *p4\n");
    }

    public static P4ServerRelease Release(string name)
    {
        P4ServerRelease.TryParse(name, out P4ServerRelease release).ShouldBeTrue();
        return release;
    }

    public static string Hash(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    public static ProcessResult RunFakeP4d(ProcessCommand command)
    {
        string content = File.Exists(command.FileName) ? File.ReadAllText(command.FileName) : string.Empty;
        return content.StartsWith("p4d ", StringComparison.Ordinal)
            ? new ProcessResult(0, $"Rev. P4D/LINUX26X86_64/{content["p4d ".Length..]} (2025/09/28).", "")
            : new ProcessResult(126, "", "cannot execute binary file");
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DaemonDbContext>
    {
        public DaemonDbContext CreateDbContext()
        {
            return new DaemonDbContext(new DbContextOptionsBuilder<DaemonDbContext>().UseSqlite(connection).Options);
        }
    }
}

/// <summary>
/// Serves the files of a fake Perforce FTP under <c>/perforce/</c>; any other path answers 404, and
/// <see cref="Unreachable"/> makes every request fail like a network error.
/// </summary>
internal sealed class FakeFtpHandler : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public int Downloads { get; private set; }

    public bool Unreachable { get; set; }

    /// <summary>
    /// Runs on every request, before it is answered.
    /// </summary>
    public Action? OnRequest { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        OnRequest?.Invoke();
        if (Unreachable)
        {
            throw new HttpRequestException("The Perforce FTP is unreachable.");
        }

        string path = request.RequestUri!.AbsolutePath["/perforce/".Length..];
        if (path.Length == 0)
        {
            string index = string.Concat(Files.Keys
                .Select(key => key.Split('/')[0])
                .Distinct()
                .Select(release => $"<a href=\"{release}/\">{release}/</a>\n"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(index) });
        }
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
