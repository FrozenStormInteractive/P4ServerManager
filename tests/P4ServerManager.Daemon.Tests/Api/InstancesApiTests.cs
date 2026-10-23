using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Tests.Api;

public sealed class InstancesApiTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        IP4ServerReleaseCatalog catalog = Substitute.For<IP4ServerReleaseCatalog>();
        catalog.GetReleasesAsync(Arg.Any<CancellationToken>()).Returns([Release("r25.1"), Release("r24.2")]);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Daemon:DataDirectory", _dataDirectory);
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(catalog));
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

    [Fact]
    public async Task GetOperation_ReturnsNotFoundForAnUnknownInstance()
    {
        HttpResponseMessage response = await _client.GetAsync(
            $"/api/instances/{Guid.NewGuid()}/operation",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StartInstance_ConflictsWhileTheInstanceIsProvisioning()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            "/api/instances",
            ValidRequest("main", GetFreePort()),
            TestContext.Current.CancellationToken);
        JsonElement instance = await created.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        HttpResponseMessage response = await _client.PostAsync(
            $"/api/instances/{instance.GetProperty("id").GetString()}/start",
            null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
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
