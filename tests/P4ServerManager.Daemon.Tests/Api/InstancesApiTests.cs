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

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Tests.Api;

public sealed class InstancesApiTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly IP4ServerReleaseCatalog _catalog = Substitute.For<IP4ServerReleaseCatalog>();
    private readonly IP4ServerBuildCache _buildCache = Substitute.For<IP4ServerBuildCache>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _catalog.GetReleasesAsync(Arg.Any<CancellationToken>()).Returns([Release("r25.1"), Release("r24.2")]);
        _buildCache.GetP4dPath(Arg.Any<P4ServerBuild>()).Returns(call => $"/builds/{call.Arg<P4ServerBuild>().BuildNumber}/p4d");
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, string.Empty, string.Empty));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Daemon:DataDirectory", _dataDirectory);
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(_catalog));
                services.Replace(ServiceDescriptor.Singleton(_buildCache));
                services.Replace(ServiceDescriptor.Singleton(_processRunner));
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
        instance.GetProperty("ssl").GetBoolean().ShouldBeFalse();
        instance.GetRawText().ShouldNotContain("secret-password");

        JsonElement instances = await _client.GetFromJsonAsync<JsonElement>(
            "/api/instances",
            TestContext.Current.CancellationToken);
        instances.EnumerateArray().Select(i => i.GetProperty("name").GetString()).ShouldBe(["main"]);
    }

    [Fact]
    public async Task CreateInstance_PutsTheRootInTheServerDirectory()
    {
        string serverDirectory = Path.Combine(_dataDirectory, "servers", "main");
        Guid id = await CreateFailedInstanceAsync("main", serverDirectory, ssl: true);

        JsonElement instance = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{id}",
            TestContext.Current.CancellationToken);
        instance.GetProperty("rootPath").GetString().ShouldBe(Path.Combine(serverDirectory, "root"));
        instance.GetProperty("ssl").GetBoolean().ShouldBeTrue();
        instance.GetProperty("sslFingerprint").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task CreateInstance_RejectsAServerDirectoryThatIsNotEmpty()
    {
        string serverDirectory = Path.Combine(_dataDirectory, "servers", "main");
        Directory.CreateDirectory(Path.Combine(serverDirectory, "root"));

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/instances",
            new
            {
                name = "main",
                release = "r25.1",
                port = GetFreePort(),
                serverDirectory,
                superUser = new { name = "admin", password = "secret-password" },
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problem.GetProperty("errors").GetProperty("serverDirectory")[0].GetString()
            .ShouldBe("The server directory is not empty.");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task CreateInstance_EnablesTheAutoStartUnlessTurnedOff(bool? autoStart, bool expected)
    {
        Dictionary<string, object?> request = new()
        {
            ["name"] = "main",
            ["release"] = "r25.1",
            ["port"] = GetFreePort(),
            ["superUser"] = new { name = "admin", password = "secret-password" },
        };
        if (autoStart is not null)
        {
            request["autoStart"] = autoStart;
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/instances",
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement instance = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        instance.GetProperty("autoStart").GetBoolean().ShouldBe(expected);
    }

    [Fact]
    public async Task UpdateInstance_TurnsTheAutoStartOffAndOnAgain()
    {
        Guid id = await CreateFailedInstanceAsync("main");

        foreach (bool autoStart in new[] { false, true })
        {
            HttpResponseMessage response = await _client.PatchAsJsonAsync(
                $"/api/instances/{id}",
                new { autoStart },
                TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonElement instance = await _client.GetFromJsonAsync<JsonElement>(
                $"/api/instances/{id}",
                TestContext.Current.CancellationToken);
            instance.GetProperty("autoStart").GetBoolean().ShouldBe(autoStart);
        }
    }

    [Fact]
    public async Task UpdateInstance_RejectsARequestWithoutSetting()
    {
        Guid id = await CreateFailedInstanceAsync("main");

        HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/instances/{id}",
            new { },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["autoStart"]);
    }

    [Fact]
    public async Task UpdateInstance_ReturnsNotFoundForAnUnknownInstance()
    {
        HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/instances/{Guid.NewGuid()}",
            new { autoStart = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("1abc", 0, "r25.1", "admin", "secret-password", "name")]
    [InlineData("main", 80, "r25.1", "admin", "secret-password", "port")]
    [InlineData("main", 70000, "r25.1", "admin", "secret-password", "port")]
    [InlineData("main", 0, "r12.1", "admin", "secret-password", "release")]
    [InlineData("main", 0, "r25.1", "admin", "short", "superUser.password")]
    [InlineData("main", 0, "r25.1", "admin", "onlylowercase", "superUser.password")]
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
    public async Task DeleteInstance_KeepsAServerDirectoryChosenAtCreation()
    {
        string serverDirectory = Path.Combine(_dataDirectory, "custom-server");
        Guid id = await CreateFailedInstanceAsync("main", serverDirectory);
        string rootPath = Path.Combine(serverDirectory, "root");
        Directory.CreateDirectory(rootPath);

        HttpResponseMessage response = await _client.DeleteAsync(
            $"/api/instances/{id}?deleteFiles=true",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Directory.Exists(rootPath).ShouldBeTrue();
    }

    [Theory]
    [InlineData("start", "{}")]
    [InlineData("stop", """{"instanceIds":[]}""")]
    [InlineData("restart", """{"all":true,"instanceIds":["5f0079c4-12bf-4f3e-b7f5-3d8677fb10bc"]}""")]
    public async Task InstancesAction_NeedsEitherAListOrAll(string action, string body)
    {
        HttpResponseMessage response = await _client.PostAsync(
            $"/api/instances/{action}",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["instanceIds"]);
    }

    [Fact]
    public async Task InstancesAction_ReturnsNotFoundForAnUnknownInstanceAndRunsNothing()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Running);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/instances/stop",
            new { instanceIds = new[] { id, Guid.NewGuid() } },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _factory.Services.GetRequiredService<InstanceRuntimeRegistry>().Get(id).ShouldBe(InstanceRuntimeStatus.Running);
    }

    [Fact]
    public async Task StopInstances_SkipsAStoppedInstanceAndReportsOneNotReady()
    {
        Guid ready = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Stopped);
        Guid failed = await CreateFailedInstanceAsync("broken");

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/instances/stop",
            new { instanceIds = new[] { ready, failed } },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement[] results = (await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken)).EnumerateArray().ToArray();
        results.Select(r => r.GetProperty("instance").GetProperty("name").GetString()).ShouldBe(["broken", "main"]);
        results.Select(r => r.GetProperty("outcome").GetString()).ShouldBe(["Failed", "Skipped"]);
        results[0].GetProperty("error").GetString().ShouldBe("Instance broken is not ready.");
    }

    [Theory]
    [InlineData("start", InstanceRuntimeStatus.Running)]
    [InlineData("restart", InstanceRuntimeStatus.Stopped)]
    public async Task InstancesAction_WithAllSelectsTheReadyInstancesOnly(string action, InstanceRuntimeStatus status)
    {
        await CreateReadyInstanceAsync("main", status);
        await CreateFailedInstanceAsync("broken");

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{action}",
            new { all = true },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement result = (await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken)).EnumerateArray().ShouldHaveSingleItem();
        result.GetProperty("instance").GetProperty("name").GetString().ShouldBe("main");
        result.GetProperty("outcome").GetString().ShouldBe("Skipped");
    }

    [Fact]
    public async Task UpgradeInstance_UpgradesTheDatabaseWithTheNewBuild()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Stopped);
        await PinToBuildAsync(id, "r24.2", "2024.2", "2700000");
        P4ServerBuild target = await AddBuildAsync("r25.1", "2025.1", "2850123");
        _buildCache.EnsureAsync(Release("r25.1"), Arg.Any<IProgress<BuildDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(target);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{id}/upgrade",
            new { release = "r25.1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement instance = await WaitForUpgradeAsync(id);
        instance.GetProperty("release").GetString().ShouldBe("r25.1");
        instance.GetProperty("version").GetString().ShouldBe("2025.1");
        instance.GetProperty("build").GetString().ShouldBe("2850123");
        instance.GetProperty("runtimeStatus").GetString().ShouldBe("Stopped");
        instance.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);

        JsonElement operation = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{id}/operation",
            TestContext.Current.CancellationToken);
        operation.GetProperty("kind").GetString().ShouldBe("upgrade");
        operation.GetProperty("status").GetString().ShouldBe("Succeeded");
        operation.GetProperty("outcome").GetString().ShouldBe("upgraded");
        // p4d did not run: it is neither stopped nor started.
        operation.GetProperty("steps").EnumerateArray()
            .Select(s => (s.GetProperty("key").GetString(), s.GetProperty("status").GetString()))
            .ShouldBe(
            [
                ("resolve-build", "Succeeded"),
                ("stop-server", "Skipped"),
                ("upgrade-database", "Succeeded"),
                ("start-server", "Skipped"),
            ]);

        List<ProcessCommand> commands = _processRunner.ReceivedCalls()
            .Select(call => (ProcessCommand)call.GetArguments()[0]!)
            .ToList();
        commands.Select(c => (c.FileName, c.Arguments.Contains("-xu"))).ShouldBe([("/builds/2850123/p4d", true)]);
    }

    [Fact]
    public async Task UpgradeInstance_SucceedsWithoutTouchingAnInstanceAlreadyOnTheLatestBuild()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Stopped);
        await PinToBuildAsync(id, "r25.1", "2025.1", "2850123");
        IDbContextFactory<DaemonDbContext> dbContextFactory =
            _factory.Services.GetRequiredService<IDbContextFactory<DaemonDbContext>>();
        P4ServerBuild current;
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            current = await db.Builds.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        }
        _buildCache.EnsureAsync(Release("r25.1"), Arg.Any<IProgress<BuildDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(current);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{id}/upgrade",
            new { release = "r25.1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement instance = await WaitForUpgradeAsync(id);
        instance.GetProperty("build").GetString().ShouldBe("2850123");
        instance.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);

        JsonElement operation = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{id}/operation",
            TestContext.Current.CancellationToken);
        operation.GetProperty("status").GetString().ShouldBe("Succeeded");
        operation.GetProperty("outcome").GetString().ShouldBe("up-to-date");
        operation.GetProperty("steps")[0].GetProperty("message").GetString()
            .ShouldBe("Already on p4d 2025.1/2850123, the latest build of r25.1");
        operation.GetProperty("steps").EnumerateArray().Skip(1).Select(s => s.GetProperty("status").GetString())
            .ShouldAllBe(status => status == "Skipped");
        _processRunner.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task UpgradeInstance_KeepsThePreviousBuildWhenTheDownloadFails()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Stopped);
        await PinToBuildAsync(id, "r24.2", "2024.2", "2700000");
        _buildCache.EnsureAsync(Arg.Any<P4ServerRelease>(), Arg.Any<IProgress<BuildDownloadProgress>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new P4ServerBuildException("The checksum does not match."));

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{id}/upgrade",
            new { release = "r25.1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement instance = await WaitForUpgradeAsync(id);
        instance.GetProperty("release").GetString().ShouldBe("r24.2");
        instance.GetProperty("build").GetString().ShouldBe("2700000");
        instance.GetProperty("state").GetString().ShouldBe("Ready");
        instance.GetProperty("lastError").GetString().ShouldBe("The checksum does not match.");
        _processRunner.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task UpgradeInstance_WarnsThatTheDatabaseMayBePartlyUpgradedWhenItsUpgradeFails()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Stopped);
        await PinToBuildAsync(id, "r24.2", "2024.2", "2700000");
        P4ServerBuild target = await AddBuildAsync("r25.1", "2025.1", "2850123");
        _buildCache.EnsureAsync(Release("r25.1"), Arg.Any<IProgress<BuildDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(target);
        _processRunner.RunAsync(Arg.Is<ProcessCommand>(c => c.Arguments.Contains("-xu")), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(1, string.Empty, "Database is corrupt."));

        await _client.PostAsJsonAsync(
            $"/api/instances/{id}/upgrade",
            new { release = "r25.1" },
            TestContext.Current.CancellationToken);

        JsonElement instance = await WaitForUpgradeAsync(id);
        // The previous build may no longer open the database.
        instance.GetProperty("build").GetString().ShouldBe("2850123");
        instance.GetProperty("lastError").GetString()
            .ShouldEndWith("failed: Database is corrupt. The database may be partly upgraded.");
    }

    [Fact]
    public async Task UpgradeInstance_RefusesAnOlderRelease()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Running);
        await PinToBuildAsync(id, "r25.1", "2025.1", "2850123");

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{id}/upgrade",
            new { release = "r24.2" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        _factory.Services.GetRequiredService<InstanceRuntimeRegistry>().Get(id).ShouldBe(InstanceRuntimeStatus.Running);
    }

    [Fact]
    public async Task UpgradeInstance_RejectsAReleaseThatIsNotAvailable()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Stopped);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{id}/upgrade",
            new { release = "r26.1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problem.GetProperty("errors").GetProperty("release")[0].GetString()
            .ShouldBe("r26.1 is not an available p4d release.");
    }

    [Fact]
    public async Task UpgradeInstance_ConflictsWhenTheInstanceIsNotReady()
    {
        Guid id = await CreateFailedInstanceAsync("main");

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{id}/upgrade",
            new { release = "r25.1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpgradeInstance_ReturnsNotFoundForAnUnknownInstance()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{Guid.NewGuid()}/upgrade",
            new { release = "r25.1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Reads the instance until its upgrade has ended.
    /// </summary>
    private async Task<JsonElement> WaitForUpgradeAsync(Guid id)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            JsonElement instance = await _client.GetFromJsonAsync<JsonElement>($"/api/instances/{id}", timeout.Token);
            if (instance.GetProperty("runtimeStatus").GetString() != "Upgrading")
            {
                return instance;
            }
            await Task.Delay(50, timeout.Token);
        }
    }

    private async Task<P4ServerBuild> AddBuildAsync(string release, string version, string buildNumber)
    {
        P4ServerBuild build = new()
        {
            Id = Guid.NewGuid(),
            Release = release,
            Version = version,
            BuildNumber = buildNumber,
            Platform = "linux-x64",
            Sha256 = buildNumber,
        };
        IDbContextFactory<DaemonDbContext> dbContextFactory =
            _factory.Services.GetRequiredService<IDbContextFactory<DaemonDbContext>>();
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(
            TestContext.Current.CancellationToken);
        db.Builds.Add(build);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return build;
    }

    private async Task PinToBuildAsync(Guid id, string release, string version, string buildNumber)
    {
        P4ServerBuild build = await AddBuildAsync(release, version, buildNumber);
        IDbContextFactory<DaemonDbContext> dbContextFactory =
            _factory.Services.GetRequiredService<IDbContextFactory<DaemonDbContext>>();
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(
            TestContext.Current.CancellationToken);
        await db.Instances
            .Where(i => i.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(i => i.BuildId, build.Id).SetProperty(i => i.Release, release),
                TestContext.Current.CancellationToken);
    }

    private async Task<Guid> CreateReadyInstanceAsync(string name, InstanceRuntimeStatus runtimeStatus)
    {
        Guid id = await CreateFailedInstanceAsync(name);
        IDbContextFactory<DaemonDbContext> dbContextFactory =
            _factory.Services.GetRequiredService<IDbContextFactory<DaemonDbContext>>();
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(
            TestContext.Current.CancellationToken);
        await db.Instances
            .Where(i => i.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(i => i.State, P4ServerInstanceState.Ready),
                TestContext.Current.CancellationToken);
        _factory.Services.GetRequiredService<InstanceRuntimeRegistry>().Set(id, runtimeStatus);
        return id;
    }

    private async Task<Guid> CreateFailedInstanceAsync(string name, string? serverDirectory = null, bool ssl = false)
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            "/api/instances",
            new
            {
                name,
                release = "r25.1",
                port = GetFreePort(),
                serverDirectory,
                ssl,
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
