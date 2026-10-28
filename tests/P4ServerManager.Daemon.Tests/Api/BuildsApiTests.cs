using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;
using P4ServerManager.Daemon.Tests.Services.Perforce;

using static P4ServerManager.Daemon.Tests.Services.Perforce.BuildTestEnvironment;

namespace P4ServerManager.Daemon.Tests.Api;

public sealed class BuildsApiTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly IP4ServerReleaseCatalog _catalog = Substitute.For<IP4ServerReleaseCatalog>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _catalog.GetReleasesAsync(Arg.Any<CancellationToken>()).Returns([Release("r25.1")]);
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => RunFakeP4d(call.Arg<ProcessCommand>()));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Daemon:DataDirectory", _dataDirectory);
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(_catalog));
                services.Replace(ServiceDescriptor.Singleton(_processRunner));
                services.Replace(ServiceDescriptor.Singleton(PlatformDefinition));
                services.AddHttpClient(PerforceFtpClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeFtpHandler());
                // The archive worker does not run: the requests it queues stay pending.
                services.RemoveAll<IHostedService>();
            });
        });
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportBuild_ArchivesP4dAndP4AndListsTheBuild()
    {
        HttpResponseMessage response = await ImportAsync(P4dContent("2025.1/2850123"), P4Content("2025.1/2850123"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonElement build = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        build.GetProperty("release").GetString().ShouldBe("r25.1");
        build.GetProperty("build").GetString().ShouldBe("2850123");
        build.GetProperty("source").GetString().ShouldBe("Imported");
        build.GetProperty("extracted").GetBoolean().ShouldBeFalse();
        build.GetProperty("archiveSize").GetInt64().ShouldBeGreaterThan(0);
        response.Headers.Location!.ToString().ShouldEndWith($"/api/p4d/builds/{build.GetProperty("id").GetString()}");

        JsonElement builds = await _client.GetFromJsonAsync<JsonElement>(
            "/api/p4d/builds",
            TestContext.Current.CancellationToken);
        builds.EnumerateArray().Select(b => b.GetProperty("build").GetString()).ShouldBe(["2850123"]);
    }

    [Fact]
    public async Task ImportBuild_NeedsBothP4dAndP4()
    {
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(P4dContent("2025.1/2850123")), "p4d", "p4d");

        HttpResponseMessage response = await _client.PostAsync(
            "/api/p4d/builds/import",
            form,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("errors").GetProperty("p4")[0].GetString().ShouldBe("Send p4, or the package that holds it.");
    }

    [Fact]
    public async Task ImportBuild_RefusesAFileThatIsNotAP4d()
    {
        HttpResponseMessage response = await ImportAsync("garbage"u8.ToArray(), "garbage"u8.ToArray());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("detail").GetString().ShouldStartWith("Could not read the version of p4d");
    }

    [Fact]
    public async Task ImportBuild_ConflictsWithABuildAlreadyInTheArchive()
    {
        await ImportAsync(P4dContent("2025.1/2850123"), P4Content("2025.1/2850123"));

        HttpResponseMessage response = await ImportAsync(P4dContent("2025.1/2850123"), P4Content("2025.1/2850123"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateBuild_ProtectsTheBuild()
    {
        Guid id = await ImportBuildAsync("2025.1/2850123");

        HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/p4d/builds/{id}",
            new { @protected = true },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement build = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        build.GetProperty("protected").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteBuild_RemovesAnUnusedBuild()
    {
        Guid id = await ImportBuildAsync("2025.1/2850123");

        HttpResponseMessage response = await _client.DeleteAsync(
            $"/api/p4d/builds/{id}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/p4d/builds/{id}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteBuild_ConflictsWhenAnInstanceUsesIt()
    {
        Guid id = await ImportBuildAsync("2025.1/2850123");
        IDbContextFactory<DaemonDbContext> dbContextFactory =
            _factory.Services.GetRequiredService<IDbContextFactory<DaemonDbContext>>();
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            db.Instances.Add(new P4ServerInstance
            {
                Id = Guid.NewGuid(),
                Name = "main",
                Port = 1666,
                RootPath = "/srv/main/root",
                Release = "r25.1",
                BuildId = id,
                State = P4ServerInstanceState.Ready,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        HttpResponseMessage response = await _client.DeleteAsync(
            $"/api/p4d/builds/{id}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("detail").GetString().ShouldBe("p4d 2025.1/2850123 is used by main.");
    }

    [Fact]
    public async Task DownloadBuild_RejectsAnInvalidRelease()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/p4d/builds/download",
            new { release = "25.1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DownloadBuild_QueuesTheDownload()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/p4d/builds/download",
            new { release = "r25.1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task GetPolicy_ReturnsTheDefaults()
    {
        JsonElement policy = await _client.GetFromJsonAsync<JsonElement>(
            "/api/p4d/archive/policy",
            TestContext.Current.CancellationToken);

        policy.GetProperty("autoDownload").GetBoolean().ShouldBeFalse();
        policy.GetProperty("checkIntervalHours").GetInt32().ShouldBe(6);
        policy.GetProperty("scope").GetString().ShouldBe("SelectedReleases");
        policy.GetProperty("releases").GetArrayLength().ShouldBe(0);
        policy.GetProperty("keepLatestPerRelease").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task UpdatePolicy_SavesThePolicy()
    {
        HttpResponseMessage response = await _client.PutAsJsonAsync(
            "/api/p4d/archive/policy",
            new
            {
                autoDownload = true,
                checkIntervalHours = 12,
                scope = "SelectedReleases",
                releases = new[] { "r25.2", "r25.1", "r25.1" },
                keepLatestPerRelease = 3,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement policy = await _client.GetFromJsonAsync<JsonElement>(
            "/api/p4d/archive/policy",
            TestContext.Current.CancellationToken);
        policy.GetProperty("autoDownload").GetBoolean().ShouldBeTrue();
        policy.GetProperty("checkIntervalHours").GetInt32().ShouldBe(12);
        policy.GetProperty("releases").EnumerateArray().Select(r => r.GetString()).ShouldBe(["r25.1", "r25.2"]);
        policy.GetProperty("keepLatestPerRelease").GetInt32().ShouldBe(3);

        JsonElement status = await _client.GetFromJsonAsync<JsonElement>(
            "/api/p4d/archive/status",
            TestContext.Current.CancellationToken);
        status.GetProperty("running").GetBoolean().ShouldBeFalse();
        status.GetProperty("nextCheckAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task UpdatePolicy_RejectsAnInvalidPolicy()
    {
        HttpResponseMessage response = await _client.PutAsJsonAsync(
            "/api/p4d/archive/policy",
            new
            {
                autoDownload = true,
                checkIntervalHours = 0,
                scope = "SelectedReleases",
                releases = Array.Empty<string>(),
                keepLatestPerRelease = 0,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order()
            .ShouldBe(["checkIntervalHours", "keepLatestPerRelease", "releases"]);
    }

    [Fact]
    public async Task UpdatePolicy_AcceptsNoReleaseWithoutTheAutomaticDownload()
    {
        HttpResponseMessage response = await _client.PutAsJsonAsync(
            "/api/p4d/archive/policy",
            new
            {
                autoDownload = false,
                checkIntervalHours = 6,
                scope = "SelectedReleases",
                releases = Array.Empty<string>(),
                keepLatestPerRelease = 2,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Check_QueuesACheck()
    {
        HttpResponseMessage response = await _client.PostAsync(
            "/api/p4d/archive/check",
            null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    private async Task<HttpResponseMessage> ImportAsync(byte[] p4d, byte[] p4)
    {
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(p4d), "p4d", "p4d");
        form.Add(new ByteArrayContent(p4), "p4", "p4");
        return await _client.PostAsync("/api/p4d/builds/import", form, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> ImportBuildAsync(string versionAndBuild)
    {
        HttpResponseMessage response = await ImportAsync(P4dContent(versionAndBuild), P4Content(versionAndBuild));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonElement build = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return build.GetProperty("id").GetGuid();
    }
}
