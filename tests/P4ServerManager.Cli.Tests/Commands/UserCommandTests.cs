using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class UserCommandTests
{
    private const string Password = "correct horse battery";

    private static readonly UserDto Admin = new(Guid.NewGuid(), "admin", true, DateTimeOffset.UtcNow, null);

    private static readonly UserDto Jane = new(Guid.NewGuid(), "jane", false, DateTimeOffset.UtcNow, null);

    private readonly CliRunner _cli = new();

    [Fact]
    public async Task List_ShowsTheAccounts()
    {
        SetUpList();

        CliResult result = await _cli.RunAsync("user", "list");

        result.ExitCode.ShouldBe(0);
        result.OutputLines[0].ShouldContain("ADMIN");
        result.OutputLines[0].ShouldContain("LAST SIGN-IN");
        result.OutputLines[1].ShouldStartWith("admin");
        result.OutputLines[1].ShouldContain("yes");
        result.OutputLines[2].ShouldStartWith("jane");
        result.OutputLines[2].ShouldContain("no");
    }

    [Fact]
    public async Task Create_SendsTheAccountReadFromStandardInput()
    {
        _cli.Daemon.On(
            HttpMethod.Post,
            "/api/users",
            StubDaemon.Json(HttpStatusCode.Created, Admin, CliJsonContext.Default.UserDto));
        _cli.Input = Password + "\n";

        CliResult result = await _cli.RunAsync("user", "create", "admin", "--password-stdin");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Created administrator account admin.");
        using JsonDocument request = JsonDocument.Parse(_cli.Daemon.RequestsTo(HttpMethod.Post, "/api/users").Single().Body!);
        request.RootElement.GetProperty("userName").GetString().ShouldBe("admin");
        request.RootElement.GetProperty("password").GetString().ShouldBe(Password);
        request.RootElement.GetProperty("isAdministrator").GetBoolean().ShouldBeFalse();
        result.Output.ShouldNotContain(Password);
        result.Error.ShouldNotContain(Password);
    }

    [Fact]
    public async Task Create_AsksForAnAdministrator_WithAdmin()
    {
        _cli.Daemon.On(
            HttpMethod.Post,
            "/api/users",
            StubDaemon.Json(HttpStatusCode.Created, Admin, CliJsonContext.Default.UserDto));
        _cli.Input = Password + "\n";

        CliResult result = await _cli.RunAsync("user", "create", "admin", "--password-stdin", "--admin");

        result.ExitCode.ShouldBe(0);
        using JsonDocument request = JsonDocument.Parse(_cli.Daemon.RequestsTo(HttpMethod.Post, "/api/users").Single().Body!);
        request.RootElement.GetProperty("isAdministrator").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GrantAdmin_MakesTheAccountAnAdministrator()
    {
        SetUpList();
        _cli.Daemon.On(
            HttpMethod.Put,
            $"/api/users/{Jane.Id}/administrator",
            StubDaemon.Json(HttpStatusCode.OK, Jane with { IsAdministrator = true }, CliJsonContext.Default.UserDto));

        CliResult result = await _cli.RunAsync("user", "grant-admin", "jane");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("jane is an administrator.");
        using JsonDocument request = JsonDocument.Parse(
            _cli.Daemon.RequestsTo(HttpMethod.Put, $"/api/users/{Jane.Id}/administrator").Single().Body!);
        request.RootElement.GetProperty("isAdministrator").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task RevokeAdmin_ShowsTheErrorOfTheDaemon_ForTheLastAdministrator()
    {
        SetUpList();
        _cli.Daemon.On(
            HttpMethod.Put,
            $"/api/users/{Admin.Id}/administrator",
            StubDaemon.Problem(HttpStatusCode.Conflict, "This is the last administrator."));

        CliResult result = await _cli.RunAsync("user", "revoke-admin", "admin");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("This is the last administrator.");
        using JsonDocument request = JsonDocument.Parse(
            _cli.Daemon.RequestsTo(HttpMethod.Put, $"/api/users/{Admin.Id}/administrator").Single().Body!);
        request.RootElement.GetProperty("isAdministrator").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Create_RefusesAShortPassword_WithoutCallingTheDaemon()
    {
        _cli.Input = "short\n";

        CliResult result = await _cli.RunAsync("user", "create", "admin", "--password-stdin");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain(UserPassword.Requirement);
        _cli.Daemon.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_RefusesPasswordsThatDoNotMatch()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.Secrets.Enqueue(Password);
        _cli.Prompter.Secrets.Enqueue("another long password");

        CliResult result = await _cli.RunAsync("user", "create", "admin");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("do not match");
        _cli.Daemon.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task SetPassword_FindsTheAccountWhateverTheCaseOfItsName()
    {
        SetUpList();
        _cli.Daemon.On(HttpMethod.Put, $"/api/users/{Admin.Id}/password", StubDaemon.Status(HttpStatusCode.NoContent));
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.Secrets.Enqueue(Password);
        _cli.Prompter.Secrets.Enqueue(Password);

        CliResult result = await _cli.RunAsync("user", "set-password", "ADMIN");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Set the password of admin.");
        _cli.Daemon.RequestsTo(HttpMethod.Put, $"/api/users/{Admin.Id}/password").Single().Body!.ShouldContain(Password);
    }

    [Fact]
    public async Task Delete_ShowsTheErrorOfTheDaemon_ForTheLastAdministrator()
    {
        SetUpList();
        _cli.Daemon.On(
            HttpMethod.Delete,
            $"/api/users/{Admin.Id}",
            StubDaemon.Problem(HttpStatusCode.Conflict, "This is the last administrator."));

        CliResult result = await _cli.RunAsync("user", "delete", "admin", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("This is the last administrator.");
    }

    [Fact]
    public async Task Delete_FailsForAnUnknownAccount()
    {
        SetUpList();

        CliResult result = await _cli.RunAsync("user", "delete", "nobody", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("No account is named nobody");
    }

    private void SetUpList()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/users",
            StubDaemon.Json<IReadOnlyList<UserDto>>(HttpStatusCode.OK, [Admin, Jane], CliJsonContext.Default.IReadOnlyListUserDto));
    }
}
