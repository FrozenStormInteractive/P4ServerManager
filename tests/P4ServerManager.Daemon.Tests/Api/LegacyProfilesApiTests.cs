using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Tests.Api;

public sealed class LegacyProfilesApiTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly IP4ServerReleaseCatalog _catalog = Substitute.For<IP4ServerReleaseCatalog>();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private string ProfilesDirectory => new DaemonPaths(_dataDirectory).LegacyProfilesDirectory;

    public ValueTask InitializeAsync()
    {
        P4ServerRelease.TryParse("r25.1", out P4ServerRelease release);
        _catalog.GetReleasesAsync(Arg.Any<CancellationToken>()).Returns([release]);

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
    public async Task GetLegacyProfiles_IsEmptyWithoutImportFolder()
    {
        JsonElement profiles = await GetProfilesAsync();

        profiles.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task GetLegacyProfiles_ListsTheProfilesWithTheirProblems()
    {
        string root = CreateRoot("main");
        WriteProfile("main.json", "main", root, "1666", "-C1");
        WriteProfile("empty.json", "empty", Path.Combine(_dataDirectory, "missing-root"), "1667");
        Directory.CreateDirectory(Path.Combine(ProfilesDirectory, "dismissed"));

        JsonElement profiles = await GetProfilesAsync();

        profiles.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ShouldBe(["empty.json", "main.json"]);
        JsonElement empty = profiles[0];
        empty.GetProperty("problems")[0].GetString()!.ShouldContain("does not exist");
        JsonElement main = profiles[1];
        main.GetProperty("name").GetString().ShouldBe("main");
        main.GetProperty("rootPath").GetString().ShouldBe(root);
        main.GetProperty("port").GetInt32().ShouldBe(1666);
        main.GetProperty("caseSensitive").GetBoolean().ShouldBeFalse();
        main.GetProperty("autoStart").GetBoolean().ShouldBeTrue();
        main.GetProperty("problems").GetArrayLength().ShouldBe(0);
        main.GetProperty("instanceId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetLegacyProfiles_ReportsARootWithoutDatabase()
    {
        string root = Path.Combine(_dataDirectory, "not-a-root");
        Directory.CreateDirectory(root);
        WriteProfile("main.json", "main", root, "1666");

        JsonElement profiles = await GetProfilesAsync();

        profiles[0].GetProperty("problems")[0].GetString().ShouldBe($"The root {root} holds no p4d database.");
    }

    [Fact]
    public async Task GetLegacyProfiles_ReportsAServerFolderTheDaemonCannotWriteTo()
    {
        // File modes only restrict a user that is not root, on Linux.
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            return;
        }

        string root = CreateRoot("main");
        string archives = Path.Combine(Path.GetDirectoryName(root)!, "archives");
        Directory.CreateDirectory(archives);
        File.SetUnixFileMode(archives, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        WriteProfile("main.json", "main", root, "1666");

        try
        {
            JsonElement profiles = await GetProfilesAsync();

            profiles[0].GetProperty("problems")[0].GetString().ShouldBe(
                $"The daemon cannot write to {archives}. Give the account it runs as access to it.");
        }
        finally
        {
            File.SetUnixFileMode(archives, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task ImportLegacyProfile_CreatesAnInstanceThatKeepsTheRoot()
    {
        string root = CreateRoot("main");
        WriteProfile("main.json", "main", root, "1666", "-C1");
        int port = GetFreePort();

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/legacy-profiles/main.json/import",
            new { name = "main", release = "r25.1", port, caseSensitive = false, autoStart = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement instance = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        string id = instance.GetProperty("id").GetString()!;
        response.Headers.Location!.ToString().ShouldEndWith($"/api/instances/{id}");
        instance.GetProperty("state").GetString().ShouldBe("Provisioning");
        instance.GetProperty("rootPath").GetString().ShouldBe(root);
        instance.GetProperty("port").GetInt32().ShouldBe(port);
        instance.GetProperty("caseSensitive").GetBoolean().ShouldBeFalse();
        instance.GetProperty("autoStart").GetBoolean().ShouldBeFalse();

        // The profile stays, marked with its instance.
        JsonElement profiles = await GetProfilesAsync();
        profiles[0].GetProperty("instanceId").GetString().ShouldBe(id);
        File.Exists(Path.Combine(ProfilesDirectory, "main.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task ImportLegacyProfile_QueuesAnImport()
    {
        string root = CreateRoot("main");
        WriteProfile("main.json", "main", root, "1666");

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/legacy-profiles/main.json/import",
            new { name = "main", release = "r25.1", port = GetFreePort() },
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        ProvisioningQueue queue = _factory.Services.GetRequiredService<ProvisioningQueue>();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await foreach (ProvisioningRequest request in queue.ReadAllAsync(timeout.Token))
        {
            request.Kind.ShouldBe(ProvisioningKind.Import);
            request.SuperUser.ShouldBeNull();
            break;
        }
    }

    [Fact]
    public async Task ImportLegacyProfile_RefusesARootAlreadyImported()
    {
        string root = CreateRoot("main");
        WriteProfile("main.json", "main", root, "1666");
        WriteProfile("copy.json", "copy", root, "1667");
        (await ImportAsync("main.json", "main")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        HttpResponseMessage response = await ImportAsync("copy.json", "copy");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problem.GetProperty("detail").GetString().ShouldBe($"An instance already uses the root {root}.");
    }

    [Fact]
    public async Task ImportLegacyProfile_RefusesAProfileWithProblems()
    {
        WriteProfile("main.json", "main", Path.Combine(_dataDirectory, "missing-root"), "1666");

        HttpResponseMessage response = await ImportAsync("main.json", "main");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("missing.json")]
    [InlineData("..%2Fp4servermanager.db")]
    public async Task ImportLegacyProfile_ReturnsNotFoundForAnUnknownProfile(string id)
    {
        HttpResponseMessage response = await ImportAsync(id, "main");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ImportLegacyProfile_ValidatesTheSettings()
    {
        string root = CreateRoot("main");
        WriteProfile("main.json", "main", root, "1666");

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/legacy-profiles/main.json/import",
            new { name = "1-bad", release = "r10.1", port = 80 },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement errors = (await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken)).GetProperty("errors");
        errors.EnumerateObject().Select(e => e.Name).Order().ShouldBe(["name", "port", "release"]);
    }

    [Fact]
    public async Task DismissLegacyProfile_MovesTheProfileAside()
    {
        WriteProfile("main.json", "main", CreateRoot("main"), "1666");

        HttpResponseMessage response = await _client.DeleteAsync(
            "/api/legacy-profiles/main.json",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetProfilesAsync()).GetArrayLength().ShouldBe(0);
        File.Exists(Path.Combine(ProfilesDirectory, "dismissed", "main.json")).ShouldBeTrue();

        (await _client.DeleteAsync("/api/legacy-profiles/main.json", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Provisioner_ResolvesTheStepsOfBothKinds()
    {
        _factory.Services.GetRequiredService<InstanceProvisioner>().ShouldNotBeNull();
        _factory.Services
            .GetKeyedServices<Daemon.Services.Operations.IOperationStep<ProvisioningContext>>(ProvisioningKind.Import)
            .Select(step => step.Key)
            .ShouldBe(["resolve-build", "check-root", "save-checkpoint", "read-settings", "upgrade-database", "start-server"]);
    }

    private async Task<JsonElement> GetProfilesAsync()
    {
        return await _client.GetFromJsonAsync<JsonElement>(
            "/api/legacy-profiles",
            TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> ImportAsync(string id, string name)
    {
        return _client.PostAsJsonAsync(
            $"/api/legacy-profiles/{id}/import",
            new { name, release = "r25.1", port = GetFreePort() },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A root that passes the checks of the daemon: a folder with a db.counters file.
    /// </summary>
    private string CreateRoot(string name)
    {
        string root = Path.Combine(_dataDirectory, "legacy", name, "root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "db.counters"), string.Empty);
        return root;
    }

    private void WriteProfile(string fileName, string name, string root, string port, string args = "")
    {
        Directory.CreateDirectory(ProfilesDirectory);
        string json = JsonSerializer.Serialize(new
        {
            Name = name,
            ServerType = "p4d",
            Args = args,
            Enabled = true,
            Environment = new Dictionary<string, string> { ["P4ROOT"] = root, ["P4PORT"] = port },
        });
        File.WriteAllText(Path.Combine(ProfilesDirectory, fileName), json);
    }

    private static int GetFreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
