using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Hosting;
using P4ServerManager.Daemon.Services.Authentication;

namespace P4ServerManager.Daemon.Tests.Api;

public sealed class AuthApiTests : IAsyncLifetime
{
    private const string Password = "correct horse battery";

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _settings = [];
    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory => _factory ??= CreateFactory();

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataDirectory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Session_TellsThatTheFirstAccountIsMissing_WhileNoAccountExists()
    {
        HttpClient client = CreateBrowserClient();

        JsonElement session = await client.GetFromJsonAsync<JsonElement>(
            "api/auth/session",
            TestContext.Current.CancellationToken);

        session.GetProperty("setupRequired").GetBoolean().ShouldBeTrue();
        session.GetProperty("user").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Api_AsksForASession_OnTheTcpPort()
    {
        await CreateAccountAsync("admin");
        HttpClient client = CreateBrowserClient();

        HttpResponseMessage instances = await client.GetAsync("api/instances", TestContext.Current.CancellationToken);
        HttpResponseMessage hub = await client.PostAsync(
            "hubs/instances/negotiate?negotiateVersion=1",
            null,
            TestContext.Current.CancellationToken);

        instances.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        hub.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_AsksNoSession_ThroughTheSocket()
    {
        // The test server gives no IP address to its requests, as Kestrel does for the socket.
        _settings["SimulateTcp"] = "false";
        HttpClient client = Factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("api/users", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Setup_CreatesTheFirstAccount_AndSignsItIn()
    {
        HttpClient client = CreateBrowserClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "api/auth/setup",
            new { userName = "admin", password = Password },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonElement session = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        session.GetProperty("user").GetProperty("userName").GetString().ShouldBe("admin");
        session.GetProperty("user").GetProperty("lastSignInAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        (await client.GetAsync("api/users", TestContext.Current.CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Setup_IsRefused_OnceAnAccountExists()
    {
        await CreateAccountAsync("admin");
        HttpClient client = CreateBrowserClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "api/auth/setup",
            new { userName = "intruder", password = Password },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.GetAsync("api/users", TestContext.Current.CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Setup_RefusesAShortPassword()
    {
        HttpClient client = CreateBrowserClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "api/auth/setup",
            new { userName = "admin", password = "short" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("errors").TryGetProperty("password", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Login_SignsIn_WithTheRightPassword_WhateverTheCaseOfTheName()
    {
        await CreateAccountAsync("admin");
        HttpClient client = CreateBrowserClient();

        HttpResponseMessage response = await LoginAsync(client, "ADMIN", Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement session = await client.GetFromJsonAsync<JsonElement>(
            "api/auth/session",
            TestContext.Current.CancellationToken);
        session.GetProperty("user").GetProperty("userName").GetString().ShouldBe("admin");
        session.GetProperty("user").GetProperty("lastSignInAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("admin", "wrong password")]
    [InlineData("nobody", Password)]
    public async Task Login_Fails_WithTheSameMessage_ForAWrongPasswordOrAnUnknownName(string userName, string password)
    {
        await CreateAccountAsync("admin");
        HttpClient client = CreateBrowserClient();

        HttpResponseMessage response = await LoginAsync(client, userName, password);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("detail").GetString().ShouldBe("The user name or the password is not correct.");
        (await client.GetAsync("api/instances", TestContext.Current.CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_IsLimited_PerMinute()
    {
        _settings["Authentication:SignInAttemptsPerMinute"] = "2";
        await CreateAccountAsync("admin");
        HttpClient client = CreateBrowserClient();

        await LoginAsync(client, "admin", "wrong password");
        await LoginAsync(client, "admin", "wrong password");
        HttpResponseMessage response = await LoginAsync(client, "admin", Password);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Request_IsForbidden_WithoutTheHeaderOfTheWebInterface()
    {
        await CreateAccountAsync("admin");
        HttpClient client = Factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "api/auth/login",
            new { userName = "admin", password = Password },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Hub_IsForbidden_FromAnotherOrigin()
    {
        await CreateAccountAsync("admin");
        HttpClient client = CreateBrowserClient();
        await LoginAsync(client, "admin", Password);
        using HttpRequestMessage request = new(HttpMethod.Post, "hubs/instances/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "http://localhost:5000");

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        await CreateAccountAsync("admin");
        HttpClient client = CreateBrowserClient();
        await LoginAsync(client, "admin", Password);

        HttpResponseMessage response = await client.PostAsync("api/auth/logout", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("api/instances", TestContext.Current.CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangePassword_EndsTheOtherSessions_AndKeepsThisOne()
    {
        await CreateAccountAsync("admin");
        HttpClient current = CreateBrowserClient();
        HttpClient other = CreateBrowserClient();
        await LoginAsync(current, "admin", Password);
        await LoginAsync(other, "admin", Password);

        HttpResponseMessage response = await current.PutAsJsonAsync(
            "api/auth/password",
            new { currentPassword = Password, newPassword = "another long password" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await current.GetAsync("api/users", TestContext.Current.CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await other.GetAsync("api/users", TestContext.Current.CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync(CreateBrowserClient(), "admin", "another long password")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_RefusesAWrongCurrentPassword()
    {
        await CreateAccountAsync("admin");
        HttpClient client = CreateBrowserClient();
        await LoginAsync(client, "admin", Password);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "api/auth/password",
            new { currentPassword = "wrong password", newPassword = "another long password" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("errors").TryGetProperty("currentPassword", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task InitialUserFile_CreatesTheFirstAccount_AndIsDeleted()
    {
        string file = Path.Combine(_dataDirectory, "initial-user");
        await File.WriteAllTextAsync(file, "admin\n" + Password + "\n", TestContext.Current.CancellationToken);
        _settings["Authentication:InitialUserFile"] = file;
        HttpClient client = CreateBrowserClient();

        HttpResponseMessage response = await LoginAsync(client, "admin", Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        File.Exists(file).ShouldBeFalse();
    }

    [Fact]
    public async Task InitialUserFile_IsIgnoredAndDeleted_WhenItsPasswordIsTooShort()
    {
        string file = Path.Combine(_dataDirectory, "initial-user");
        await File.WriteAllTextAsync(file, "admin\nshort", TestContext.Current.CancellationToken);
        _settings["Authentication:InitialUserFile"] = file;
        HttpClient client = CreateBrowserClient();

        JsonElement session = await client.GetFromJsonAsync<JsonElement>(
            "api/auth/session",
            TestContext.Current.CancellationToken);

        session.GetProperty("setupRequired").GetBoolean().ShouldBeTrue();
        File.Exists(file).ShouldBeFalse();
    }

    [Fact]
    public async Task Users_AreManagedBySignedInUsers()
    {
        await CreateAccountAsync("admin");
        HttpClient client = CreateBrowserClient();
        await LoginAsync(client, "admin", Password);

        HttpResponseMessage created = await client.PostAsJsonAsync(
            "api/users",
            new { userName = "jane", password = Password },
            TestContext.Current.CancellationToken);
        HttpResponseMessage duplicate = await client.PostAsJsonAsync(
            "api/users",
            new { userName = "JANE", password = Password },
            TestContext.Current.CancellationToken);
        JsonElement jane = await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        HttpResponseMessage reset = await client.PutAsJsonAsync(
            $"api/users/{jane.GetProperty("id").GetGuid()}/password",
            new { password = "a brand new password" },
            TestContext.Current.CancellationToken);
        JsonElement users = await client.GetFromJsonAsync<JsonElement>("api/users", TestContext.Current.CancellationToken);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        users.EnumerateArray().Select(u => u.GetProperty("userName").GetString()).ShouldBe(["admin", "jane"]);
        users[0].TryGetProperty("passwordHash", out _).ShouldBeFalse();
        (await LoginAsync(CreateBrowserClient(), "jane", "a brand new password")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteUser_KeepsTheLastAccount_AndEndsTheSessionsOfADeletedOne()
    {
        User admin = await CreateAccountAsync("admin");
        User jane = await CreateAccountAsync("jane");
        HttpClient adminClient = CreateBrowserClient();
        HttpClient janeClient = CreateBrowserClient();
        await LoginAsync(adminClient, "admin", Password);
        await LoginAsync(janeClient, "jane", Password);

        HttpResponseMessage deleted = await adminClient.DeleteAsync(
            $"api/users/{jane.Id}",
            TestContext.Current.CancellationToken);
        HttpResponseMessage last = await adminClient.DeleteAsync(
            $"api/users/{admin.Id}",
            TestContext.Current.CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        last.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await janeClient.GetAsync("api/instances", TestContext.Current.CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Accounts_AreStoredWithAHashedPassword()
    {
        await CreateAccountAsync("admin");

        IDbContextFactory<DaemonDbContext> dbContextFactory =
            Factory.Services.GetRequiredService<IDbContextFactory<DaemonDbContext>>();
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        User stored = await db.Users.SingleAsync(TestContext.Current.CancellationToken);
        stored.PasswordHash.ShouldNotContain(Password);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        byte[] database = await File.ReadAllBytesAsync(
            Path.Combine(_dataDirectory, "p4servermanager.db"),
            TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(database).ShouldNotContain(Password);
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        bool simulateTcp = !_settings.TryGetValue("SimulateTcp", out string? value) || value != "false";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Daemon:DataDirectory", _dataDirectory);
            foreach ((string key, string? setting) in _settings)
            {
                builder.UseSetting(key, setting);
            }
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                if (simulateTcp)
                {
                    services.AddSingleton<IStartupFilter, TcpConnectionStartupFilter>();
                }
            });
        });
    }

    // A client like the web interface: it keeps the cookies and sends the header of the web interface.
    private HttpClient CreateBrowserClient()
    {
        HttpClient client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(BrowserRequestGuard.HeaderName, "1");
        return client;
    }

    private Task<User> CreateAccountAsync(string userName)
    {
        return Factory.Services.GetRequiredService<UserService>()
            .CreateAsync(userName, Password, firstOnly: false, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password)
    {
        return client.PostAsJsonAsync(
            "api/auth/login",
            new { userName, password },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Gives the requests of the test server an IP address, as Kestrel does for the TCP port: without one, they pass
    /// for requests through the socket of the CLI.
    /// </summary>
    private sealed class TcpConnectionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.LocalIpAddress = IPAddress.Loopback;
                    context.Connection.RemoteIpAddress = IPAddress.Loopback;
                    return nextMiddleware(context);
                });
                next(app);
            };
        }
    }
}
