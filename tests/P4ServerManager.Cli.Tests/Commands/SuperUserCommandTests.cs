using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class SuperUserCommandTests
{
    // Weak on purpose: the password already exists in P4, the CLI does not judge its strength.
    private const string Password = "weakpass";

    private readonly CliRunner _cli = new();

    [Fact]
    public async Task Set_SendsTheAccountReadFromStandardInput()
    {
        InstanceDto main = CliRunner.Instance("main");
        SetUp(main, main with { SuperUser = new SuperUserDto("admin", SuperUserStatus.Valid, DateTimeOffset.UtcNow, null) });
        _cli.Input = Password + "\n";

        CliResult result = await _cli.RunAsync(
            "instance", "super-user", "set", "main", "--user", "admin", "--password-stdin");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("logs in with super access");
        using JsonDocument request = JsonDocument.Parse(RequestBody(main));
        request.RootElement.GetProperty("userName").GetString().ShouldBe("admin");
        request.RootElement.GetProperty("password").GetString().ShouldBe(Password);
        result.Output.ShouldNotContain(Password);
        result.Error.ShouldNotContain(Password);
    }

    [Fact]
    public async Task Set_AsksThePasswordTwiceOnATerminal()
    {
        InstanceDto main = CliRunner.Instance("main", runtimeStatus: InstanceRuntimeStatus.Stopped);
        SetUp(main, main with { SuperUser = new SuperUserDto("admin", SuperUserStatus.Unchecked, null, null) });
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.Secrets.Enqueue(Password);
        _cli.Prompter.Secrets.Enqueue(Password);

        CliResult result = await _cli.RunAsync("instance", "super-user", "set", "main", "--user", "admin");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("checked once the server runs");
        RequestBody(main).ShouldContain(Password);
    }

    [Fact]
    public async Task Set_RefusesPasswordsThatDoNotMatch()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.Secrets.Enqueue(Password);
        _cli.Prompter.Secrets.Enqueue("another");

        CliResult result = await _cli.RunAsync("instance", "super-user", "set", "main", "--user", "admin");

        result.ExitCode.ShouldNotBe(0);
        result.Error.ShouldContain("do not match");
        _cli.Daemon.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_FailsWhenTheAccountIsNotUsable()
    {
        InstanceDto main = CliRunner.Instance("main");
        InstanceDto updated = main with
        {
            SuperUser = new SuperUserDto("admin", SuperUserStatus.InvalidCredentials, DateTimeOffset.UtcNow, "Password invalid."),
        };
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                [main],
                CliJsonContext.Default.IReadOnlyListInstanceDto));
        _cli.Daemon.On(
            HttpMethod.Post,
            $"/api/instances/{main.Id}/super-user/check",
            StubDaemon.Json(HttpStatusCode.OK, updated, CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync("instance", "super-user", "check", "main");

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("InvalidCredentials");
        result.Output.ShouldContain("Password invalid.");
    }

    private void SetUp(InstanceDto instance, InstanceDto updated)
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                [instance],
                CliJsonContext.Default.IReadOnlyListInstanceDto));
        _cli.Daemon.On(
            HttpMethod.Put,
            $"/api/instances/{instance.Id}/super-user",
            StubDaemon.Json(HttpStatusCode.OK, updated, CliJsonContext.Default.InstanceDto));
    }

    private string RequestBody(InstanceDto instance)
    {
        return _cli.Daemon.RequestsTo(HttpMethod.Put, $"/api/instances/{instance.Id}/super-user").Single().Body!;
    }
}
