using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
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
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Tests.Api;

public sealed class SuperUserApiTests : IAsyncLifetime
{
    private const string CreationPassword = "secret-password";

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly IP4ServerReleaseCatalog _catalog = Substitute.For<IP4ServerReleaseCatalog>();
    private readonly IP4ServerBuildCache _buildCache = Substitute.For<IP4ServerBuildCache>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly List<ProcessCommand> _commands = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    // What the fake p4 answers to "p4 login" and to "p4 protects -m".
    private ProcessResult _login = new(0, "User admin logged in.", string.Empty);
    private ProcessResult _protects = new(0, "... permMax super\n", string.Empty);

    public ValueTask InitializeAsync()
    {
        _catalog.GetReleasesAsync(Arg.Any<CancellationToken>()).Returns([Release("r25.1")]);
        _buildCache.GetP4Path(Arg.Any<P4ServerBuild>()).Returns("/builds/p4");
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                ProcessCommand command = call.Arg<ProcessCommand>();
                lock (_commands)
                {
                    _commands.Add(command);
                }
                return command.Arguments.Contains("login") ? _login
                    : command.Arguments.Contains("protects") ? _protects
                    : new ProcessResult(0, string.Empty, string.Empty);
            });

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Daemon:DataDirectory", _dataDirectory);
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(_catalog));
                services.Replace(ServiceDescriptor.Singleton(_buildCache));
                services.Replace(ServiceDescriptor.Singleton(_processRunner));
                services.Replace(ServiceDescriptor.Singleton(P4ServerPlatformDefinition.LinuxX64));
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
            foreach (string file in Directory.EnumerateFiles(_dataDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateInstance_StoresTheSuperUserWithItsPasswordEncrypted()
    {
        Guid id = await CreateInstanceAsync("main");

        JsonElement instance = await GetInstanceAsync(id);
        JsonElement superUser = instance.GetProperty("superUser");
        superUser.GetProperty("userName").GetString().ShouldBe("admin");
        superUser.GetProperty("status").GetString().ShouldBe("Unchecked");
        instance.GetRawText().ShouldNotContain(CreationPassword);

        InstanceSuperUser stored = await GetStoredAsync(id);
        stored.ProtectedPassword.ShouldNotContain(CreationPassword);
        _factory.Services.GetRequiredService<SuperUserPasswordProtector>().Unprotect(stored.ProtectedPassword)
            .ShouldBe(CreationPassword);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        byte[] database = await File.ReadAllBytesAsync(
            _factory.Services.GetRequiredService<DaemonPaths>().DatabasePath,
            TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(database).ShouldNotContain(CreationPassword);
    }

    [Fact]
    public async Task SetSuperUser_StoresAnAccountThatLogsInWithSuperAccess()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Running);

        HttpResponseMessage response = await SetSuperUserAsync(id, "root", "new-password");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement superUser = (await ReadJsonAsync(response)).GetProperty("superUser");
        superUser.GetProperty("userName").GetString().ShouldBe("root");
        superUser.GetProperty("status").GetString().ShouldBe("Valid");
        superUser.GetProperty("checkedAt").ValueKind.ShouldBe(JsonValueKind.String);

        ProcessCommand login = LoggedCommands("login").Single();
        login.StandardInput.ShouldBe("new-password\n");
        login.Arguments.ShouldNotContain("new-password");
        login.Arguments.ShouldContain("root");
        LoggedCommands("logout").ShouldHaveSingleItem();

        InstanceSuperUser stored = await GetStoredAsync(id);
        _factory.Services.GetRequiredService<SuperUserPasswordProtector>().Unprotect(stored.ProtectedPassword)
            .ShouldBe("new-password");
    }

    [Fact]
    public async Task SetSuperUser_RefusesAWrongPassword()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Running);
        _login = new ProcessResult(1, string.Empty, "Password invalid.");

        HttpResponseMessage response = await SetSuperUserAsync(id, "root", "wrong");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("errors").GetProperty("password")[0].GetString()
            .ShouldBe("Password invalid.");
        (await GetStoredAsync(id)).UserName.ShouldBe("admin");
    }

    [Fact]
    public async Task SetSuperUser_RefusesAnAccountWithoutSuperAccess()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Running);
        _protects = new ProcessResult(0, "... permMax admin\n", string.Empty);

        HttpResponseMessage response = await SetSuperUserAsync(id, "operator", "password");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("errors").GetProperty("userName")[0].GetString()!
            .ShouldContain("admin access: super access is needed");
        LoggedCommands("logout").ShouldHaveSingleItem();
        (await GetStoredAsync(id)).UserName.ShouldBe("admin");
    }

    [Fact]
    public async Task SetSuperUser_StoresTheAccountUncheckedWhenTheServerIsStopped()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Stopped);

        HttpResponseMessage response = await SetSuperUserAsync(id, "root", "weak");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement superUser = (await ReadJsonAsync(response)).GetProperty("superUser");
        superUser.GetProperty("userName").GetString().ShouldBe("root");
        superUser.GetProperty("status").GetString().ShouldBe("Unchecked");
        LoggedCommands("login").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("1nvalid", "password", "userName")]
    [InlineData("root", "", "password")]
    public async Task SetSuperUser_ValidatesTheRequest(string userName, string password, string field)
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Stopped);

        HttpResponseMessage response = await SetSuperUserAsync(id, userName, password);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        (await GetStoredAsync(id)).UserName.ShouldBe("admin");
    }

    [Fact]
    public async Task SetSuperUser_Returns404ForAnUnknownInstance()
    {
        HttpResponseMessage response = await SetSuperUserAsync(Guid.NewGuid(), "root", "password");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CheckSuperUser_ReportsAnExpiredPassword()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Running);
        _login = new ProcessResult(1, string.Empty, "Your password has expired, please change your password.");

        HttpResponseMessage response = await _client.PostAsync(
            $"/api/instances/{id}/super-user/check",
            null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement superUser = (await ReadJsonAsync(response)).GetProperty("superUser");
        superUser.GetProperty("status").GetString().ShouldBe("PasswordExpired");
        superUser.GetProperty("error").GetString()!.ShouldContain("expired");
        LoggedCommands("logout").ShouldBeEmpty();
    }

    [Fact]
    public async Task CheckSuperUser_KeepsTheStatusWhenTheServerCannotBeReached()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Running);
        _login = new ProcessResult(1, string.Empty, "Perforce client error:\n\tConnect to server failed; check $P4PORT.");

        HttpResponseMessage response = await _client.PostAsync(
            $"/api/instances/{id}/super-user/check",
            null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        InstanceSuperUser stored = await GetStoredAsync(id);
        stored.Status.ShouldBe(SuperUserStatus.Unchecked);
        stored.CheckedAt.ShouldBeNull();
    }

    [Fact]
    public async Task CheckSuperUser_Returns409WhenTheServerIsStopped()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Stopped);

        HttpResponseMessage response = await _client.PostAsync(
            $"/api/instances/{id}/super-user/check",
            null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task TryCheck_DoesNotTryAnAccountFoundInvalidAgain()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Running);
        _login = new ProcessResult(1, string.Empty, "Password invalid.");
        SuperUserService superUsers = _factory.Services.GetRequiredService<SuperUserService>();

        (await superUsers.TryCheckAsync(id, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await GetStoredAsync(id)).Status.ShouldBe(SuperUserStatus.InvalidCredentials);

        (await superUsers.TryCheckAsync(id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        LoggedCommands("login").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Monitor_ChecksTheSuperUserWhenTheServerStarts()
    {
        Guid id = await CreateReadyInstanceAsync("main", InstanceRuntimeStatus.Stopped);
        SuperUserMonitor monitor = _factory.Services.GetRequiredService<SuperUserMonitor>();
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            _factory.Services.GetRequiredService<InstanceRuntimeRegistry>().Set(id, InstanceRuntimeStatus.Running);

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while ((await GetStoredAsync(id)).Status == SuperUserStatus.Unchecked && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            (await GetStoredAsync(id)).Status.ShouldBe(SuperUserStatus.Valid);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    private List<ProcessCommand> LoggedCommands(string p4Command)
    {
        lock (_commands)
        {
            return _commands.Where(c => c.Arguments.Contains(p4Command)).ToList();
        }
    }

    private Task<HttpResponseMessage> SetSuperUserAsync(Guid id, string userName, string password)
    {
        return _client.PutAsJsonAsync(
            $"/api/instances/{id}/super-user",
            new { userName, password },
            TestContext.Current.CancellationToken);
    }

    private async Task<JsonElement> GetInstanceAsync(Guid id)
    {
        return await _client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{id}",
            TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private async Task<InstanceSuperUser> GetStoredAsync(Guid id)
    {
        IDbContextFactory<DaemonDbContext> dbContextFactory =
            _factory.Services.GetRequiredService<IDbContextFactory<DaemonDbContext>>();
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(
            TestContext.Current.CancellationToken);
        P4ServerInstance instance = await db.Instances
            .AsNoTracking()
            .SingleAsync(i => i.Id == id, TestContext.Current.CancellationToken);
        return instance.SuperUser!;
    }

    private async Task<Guid> CreateInstanceAsync(string name)
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            "/api/instances",
            new
            {
                name,
                release = "r25.1",
                port = GetFreePort(),
                caseSensitive = true,
                unicode = false,
                superUser = new { name = "admin", password = CreationPassword },
            },
            TestContext.Current.CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await ReadJsonAsync(created)).GetProperty("id").GetGuid();
    }

    // The provisioning does not run in these tests: the instance is made ready by hand.
    private async Task<Guid> CreateReadyInstanceAsync(string name, InstanceRuntimeStatus runtimeStatus)
    {
        Guid id = await CreateInstanceAsync(name);
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
