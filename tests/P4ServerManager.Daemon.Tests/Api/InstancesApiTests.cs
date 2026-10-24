using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using NSubstitute.ExceptionExtensions;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Tests.Api;

public sealed class InstancesApiTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly IP4ServerReleaseCatalog _catalog = Substitute.For<IP4ServerReleaseCatalog>();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _catalog.GetReleasesAsync(Arg.Any<CancellationToken>()).Returns([Release("r25.1"), Release("r24.2")]);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Daemon:DataDirectory", _dataDirectory);
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(_catalog));
                // The tests only check the API: nothing is downloaded or started.
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
    public async Task GetVersions_ListsTheReleasesOfTheCatalog()
    {
        JsonElement versions = await _client.GetFromJsonAsync<JsonElement>(
            "/api/p4d/versions",
            TestContext.Current.CancellationToken);

        versions.EnumerateArray().Select(v => v.GetProperty("release").GetString()).ShouldBe(["r25.1", "r24.2"]);
        versions[0].GetProperty("version").GetString().ShouldBe("2025.1");
        versions[0].GetProperty("cachedBuilds").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task CreateInstance_AcceptsAValidRequest()
    {
        int port = GetFreePort();

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/instances",
            ValidRequest("main", port),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement instance = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        string id = instance.GetProperty("id").GetString()!;
        response.Headers.Location!.ToString().ShouldEndWith($"/api/instances/{id}");
        instance.GetProperty("state").GetString().ShouldBe("Provisioning");
        instance.GetProperty("runtimeStatus").GetString().ShouldBe("Unknown");
        instance.GetProperty("rootPath").GetString().ShouldStartWith(_dataDirectory);
        instance.GetRawText().ShouldNotContain("secret-password");

        JsonElement instances = await _client.GetFromJsonAsync<JsonElement>(
            "/api/instances",
            TestContext.Current.CancellationToken);
        instances.EnumerateArray().Select(i => i.GetProperty("name").GetString()).ShouldBe(["main"]);
    }

    [Theory]
    [InlineData("1abc", 0, "r25.1", "admin", "secret-password", "name")]
    [InlineData("main", 80, "r25.1", "admin", "secret-password", "port")]
    [InlineData("main", 70000, "r25.1", "admin", "secret-password", "port")]
    [InlineData("main", 0, "r12.1", "admin", "secret-password", "release")]
    [InlineData("main", 0, "r25.1", "admin", "short", "superUser.password")]
    [InlineData("main", 0, "r25.1", "9admin", "secret-password", "superUser.name")]
    public async Task CreateInstance_RejectsAnInvalidField(
        string name,
        int port,
        string release,
        string userName,
        string password,
        string field)
    {
        object request = new
        {
            name,
            release,
            port = port == 0 ? GetFreePort() : port,
            caseSensitive = true,
            unicode = false,
            superUser = new { name = userName, password },
        };

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/instances",
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe([field]);
        // The placeholders of the messages, such as {MinLength}, are filled in.
        problem.GetProperty("errors").GetProperty(field)[0].GetString().ShouldNotContain("{");
    }

    [Theory]
    [InlineData("name", "name")]
    [InlineData("release", "release")]
    [InlineData("superUser", "superUser")]
    [InlineData("superUser.password", "superUser.password")]
    public async Task CreateInstance_KeysAMissingFieldByItsJsonName(string missing, string field)
    {
        Dictionary<string, object?> superUser = new() { ["name"] = "admin", ["password"] = "secret-password" };
        Dictionary<string, object?> request = new()
        {
            ["name"] = "main",
            ["release"] = "r25.1",
            ["port"] = GetFreePort(),
            ["caseSensitive"] = true,
            ["unicode"] = false,
            ["superUser"] = superUser,
        };
        string[] path = missing.Split('.');
        (path.Length == 1 ? request : superUser).Remove(path[^1]);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/instances",
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe([field]);
    }

    [Fact]
    public async Task CreateInstance_RejectsANameOrPortAlreadyTaken()
    {
        int port = GetFreePort();
        (await _client.PostAsJsonAsync("/api/instances", ValidRequest("main", port), TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/instances",
            ValidRequest("MAIN", port),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order().ShouldBe(["name", "port"]);
    }

    [Theory]
    [InlineData("GET", "/api/p4d/versions")]
    [InlineData("POST", "/api/instances")]
    public async Task ReadingTheReleases_FailsWithBadGatewayWhenThePerforceFtpIsDown(string method, string url)
    {
        _catalog.GetReleasesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("Unreachable"));
        using HttpRequestMessage request = new(new HttpMethod(method), url);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(ValidRequest("main", GetFreePort()));
        }

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task GetOperation_ReturnsNotFoundForAnUnknownInstance()
    {
        HttpResponseMessage response = await _client.GetAsync(
            $"/api/instances/{Guid.NewGuid()}/operation",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("restart")]
    public async Task Operation_ConflictsWhileTheInstanceIsProvisioning(string operation)
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            "/api/instances",
            ValidRequest("main", GetFreePort()),
            TestContext.Current.CancellationToken);
        JsonElement instance = await created.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        HttpResponseMessage response = await _client.PostAsync(
            $"/api/instances/{instance.GetProperty("id").GetString()}/{operation}",
            null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RestartInstance_ReturnsNotFoundForAnUnknownInstance()
    {
        HttpResponseMessage response = await _client.PostAsync(
            $"/api/instances/{Guid.NewGuid()}/restart",
            null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteInstance_ConflictsWhileTheInstanceIsProvisioning()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            "/api/instances",
            ValidRequest("main", GetFreePort()),
            TestContext.Current.CancellationToken);
        JsonElement instance = await created.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        HttpResponseMessage response = await _client.DeleteAsync(
            $"/api/instances/{instance.GetProperty("id").GetString()}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteInstance_ReturnsNotFoundForAnUnknownInstance()
    {
        HttpResponseMessage response = await _client.DeleteAsync(
            $"/api/instances/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteInstance_RemovesAFailedInstanceAndItsFilesOnlyWhenAsked(bool deleteFiles)
    {
        Guid id = await CreateFailedInstanceAsync("main");
        string directory = _factory.Services.GetRequiredService<DaemonPaths>().GetInstanceDirectory(id);
        Directory.CreateDirectory(Path.Combine(directory, "root"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "root", "db.counters"),
            "data",
            TestContext.Current.CancellationToken);
        // p4 makes its tickets file read-only.
        string tickets = Path.Combine(directory, "client", ".p4tickets");
        Directory.CreateDirectory(Path.GetDirectoryName(tickets)!);
        await File.WriteAllTextAsync(tickets, "ticket", TestContext.Current.CancellationToken);
        File.SetAttributes(tickets, FileAttributes.ReadOnly);

        HttpResponseMessage response = await _client.DeleteAsync(
            $"/api/instances/{id}?deleteFiles={deleteFiles}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/instances/{id}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Directory.Exists(directory).ShouldBe(!deleteFiles);
        if (!deleteFiles)
        {
            // Lets DisposeAsync delete the kept files.
            File.SetAttributes(tickets, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task DeleteInstance_KeepsARootChosenAtCreation()
    {
        string rootPath = Path.Combine(_dataDirectory, "custom-root");
        Guid id = await CreateFailedInstanceAsync("main", rootPath);
        Directory.CreateDirectory(rootPath);

        HttpResponseMessage response = await _client.DeleteAsync(
            $"/api/instances/{id}?deleteFiles=true",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Directory.Exists(rootPath).ShouldBeTrue();
    }

    private async Task<Guid> CreateFailedInstanceAsync(string name, string? rootPath = null)
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            "/api/instances",
            new
            {
                name,
                release = "r25.1",
                port = GetFreePort(),
                rootPath,
                caseSensitive = true,
                unicode = false,
                superUser = new { name = "admin", password = "secret-password" },
            },
            TestContext.Current.CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement instance = await created.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Guid id = instance.GetProperty("id").GetGuid();

        // The provisioning does not run in these tests: it is marked failed by hand.
        IDbContextFactory<DaemonDbContext> dbContextFactory =
            _factory.Services.GetRequiredService<IDbContextFactory<DaemonDbContext>>();
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(
            TestContext.Current.CancellationToken);
        await db.Instances
            .Where(i => i.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(i => i.State, P4ServerInstanceState.Failed),
                TestContext.Current.CancellationToken);
        return id;
    }

    private static object ValidRequest(string name, int port)
    {
        return new
        {
            name,
            description = "Test server",
            release = "r25.1",
            port,
            caseSensitive = false,
            unicode = false,
            superUser = new { name = "admin", password = "secret-password" },
        };
    }

    private static int GetFreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static P4ServerRelease Release(string name)
    {
        P4ServerRelease.TryParse(name, out P4ServerRelease release);
        return release;
    }
}
