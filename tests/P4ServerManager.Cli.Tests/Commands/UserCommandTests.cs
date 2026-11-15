using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class UserCommandTests
{
    private const string Password = "correct horse battery";

    private static readonly UserDto Admin = new(Guid.NewGuid(), "admin", DateTimeOffset.UtcNow, null);

    private readonly CliRunner _cli = new();

    [Fact]
    public async Task List_ShowsTheAccounts()
    {
        SetUpList();

        CliResult result = await _cli.RunAsync("user", "list");

        result.ExitCode.ShouldBe(0);
        result.OutputLines[0].ShouldContain("LAST SIGN-IN");
        result.OutputLines[1].ShouldStartWith("admin");
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
        result.Output.ShouldContain("Created account admin.");
        using JsonDocument request = JsonDocument.Parse(_cli.Daemon.RequestsTo(HttpMethod.Post, "/api/users").Single().Body!);
        request.RootElement.GetProperty("userName").GetString().ShouldBe("admin");
        request.RootElement.GetProperty("password").GetString().ShouldBe(Password);
        result.Output.ShouldNotContain(Password);
        result.Error.ShouldNotContain(Password);
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
    public async Task Delete_ShowsTheErrorOfTheDaemon_ForTheLastAccount()
    {
        SetUpList();
        _cli.Daemon.On(
            HttpMethod.Delete,
            $"/api/users/{Admin.Id}",
            StubDaemon.Problem(HttpStatusCode.Conflict, "This is the last account."));

        CliResult result = await _cli.RunAsync("user", "delete", "admin", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("This is the last account.");
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
            StubDaemon.Json<IReadOnlyList<UserDto>>(HttpStatusCode.OK, [Admin], CliJsonContext.Default.IReadOnlyListUserDto));
    }
}
