using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class CreateInstanceCommandTests
{
    private const string Password = "s3cret-pass";

    private readonly CliRunner _cli = new();
    private readonly InstanceDto _provisioning = CliRunner.Instance(
        "main",
        P4ServerInstanceState.Provisioning,
        InstanceRuntimeStatus.Unknown);

    private string InstancePath => $"/api/instances/{_provisioning.Id}";

    [Fact]
    public async Task Create_SendsTheSettings()
    {
        _cli.Input = Password + "\n";
        SetCreated();

        CliResult result = await _cli.RunAsync(
            "instance", "create", "main",
            "--release", "r25.1",
            "--port", "1666",
            "--super-user", "admin",
            "--password-stdin",
            "--case-insensitive",
            "--ssl",
            "--directory", "/volume1/p4/main",
            "--no-auto-start",
            "--description", "Main depot",
            "--no-wait");

        result.ExitCode.ShouldBe(0);
        string body = _cli.Daemon.RequestsTo(HttpMethod.Post, "/api/instances").Single().Body!;
        using JsonDocument request = JsonDocument.Parse(body);
        request.RootElement.GetProperty("name").GetString().ShouldBe("main");
        request.RootElement.GetProperty("release").GetString().ShouldBe("r25.1");
        request.RootElement.GetProperty("port").GetInt32().ShouldBe(1666);
        request.RootElement.GetProperty("caseSensitive").GetBoolean().ShouldBeFalse();
        request.RootElement.GetProperty("unicode").GetBoolean().ShouldBeFalse();
        request.RootElement.GetProperty("ssl").GetBoolean().ShouldBeTrue();
        request.RootElement.GetProperty("serverDirectory").GetString().ShouldBe("/volume1/p4/main");
        request.RootElement.GetProperty("autoStart").GetBoolean().ShouldBeFalse();
        request.RootElement.GetProperty("description").GetString().ShouldBe("Main depot");
        request.RootElement.GetProperty("superUser").GetProperty("name").GetString().ShouldBe("admin");
        request.RootElement.GetProperty("superUser").GetProperty("password").GetString().ShouldBe(Password);
        result.Output.ShouldNotContain(Password);
        result.Output.ShouldContain("instance operation main --follow");
    }

    [Fact]
    public async Task Create_AsksThePasswordTwiceOnATerminal()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.Secrets.Enqueue(Password);
        _cli.Prompter.Secrets.Enqueue(Password);
        SetCreated();

        CliResult result = await RunCreateAsync("--no-wait");

        result.ExitCode.ShouldBe(0);
        _cli.Prompter.Questions.Count.ShouldBe(2);
        _cli.Daemon.RequestsTo(HttpMethod.Post, "/api/instances").Single().Body!.ShouldContain(Password);
    }

    [Fact]
    public async Task Create_FailsWhenTheConfirmationDiffers()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.Secrets.Enqueue(Password);
        _cli.Prompter.Secrets.Enqueue("another-pass");

        CliResult result = await RunCreateAsync("--no-wait");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("The passwords do not match.");
        _cli.Daemon.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_RefusesAWeakPasswordOnATerminalBeforeItsConfirmation()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.Secrets.Enqueue("onlylowercase");

        CliResult result = await RunCreateAsync("--no-wait");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain(SuperUserPassword.Requirement);
        _cli.Prompter.Questions.Count.ShouldBe(1);
        _cli.Daemon.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Short1")]
    [InlineData("onlylowercase")]
    [InlineData("12345678")]
    public async Task Create_RefusesAWeakPasswordFromStandardInput(string password)
    {
        _cli.Input = password + "\n";

        CliResult result = await RunCreateAsync("--password-stdin", "--no-wait");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain(SuperUserPassword.Requirement);
        _cli.Daemon.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_NeedsAPasswordFromStandardInputWithoutATerminal()
    {
        CliResult result = await RunCreateAsync("--no-wait");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("--password-stdin");
        _cli.Daemon.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_WaitsForTheProvisioning()
    {
        _cli.Input = Password;
        SetCreated();
        _cli.Daemon.On(
            HttpMethod.Get,
            $"{InstancePath}/operation",
            // The provisioning worker has not taken the instance yet.
            StubDaemon.Problem(HttpStatusCode.NotFound),
            Operation(OperationStatus.Running, OperationStepStatus.Succeeded, OperationStepStatus.Running),
            Operation(OperationStatus.Succeeded, OperationStepStatus.Succeeded, OperationStepStatus.Succeeded));
        _cli.Daemon.On(
            HttpMethod.Get,
            InstancePath,
            StubDaemon.Json(HttpStatusCode.OK, _provisioning, CliJsonContext.Default.InstanceDto),
            StubDaemon.Json(HttpStatusCode.OK, Ready(), CliJsonContext.Default.InstanceDto));

        CliResult result = await RunCreateAsync("--password-stdin");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.ShouldBe(
        [
            "Creating main...",
            "[ ok ] Get the p4d build",
            "[ .. ] Start the server",
            "[ ok ] Start the server",
            "main is ready on port 1666.",
        ]);
    }

    [Fact]
    public async Task Create_FailsWhenTheProvisioningFails()
    {
        _cli.Input = Password;
        SetCreated();
        _cli.Daemon.On(
            HttpMethod.Get,
            $"{InstancePath}/operation",
            Operation(OperationStatus.Failed, OperationStepStatus.Succeeded, OperationStepStatus.Failed, "Port in use."));
        _cli.Daemon.On(
            HttpMethod.Get,
            InstancePath,
            StubDaemon.Json(
                HttpStatusCode.OK,
                _provisioning with { State = P4ServerInstanceState.Failed, LastError = "Port in use." },
                CliJsonContext.Default.InstanceDto));

        CliResult result = await RunCreateAsync("--password-stdin");

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("[fail] Start the server: Port in use.");
        result.Error.ShouldContain("Error: Port in use.");
    }

    [Fact]
    public async Task Create_ReportsTheValidationErrorsByField()
    {
        _cli.Input = Password;
        _cli.Daemon.On(
            HttpMethod.Post,
            "/api/instances",
            StubDaemon.Problem(
                HttpStatusCode.BadRequest,
                errors: new Dictionary<string, string[]> { ["port"] = ["Another instance already uses this port."] }));

        CliResult result = await RunCreateAsync("--password-stdin");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("  port: Another instance already uses this port.");
    }

    private Task<CliResult> RunCreateAsync(params string[] options)
    {
        return _cli.RunAsync(
        [
            "instance", "create", "main", "--release", "r25.1", "--port", "1666", "--super-user", "admin", .. options,
        ]);
    }

    private void SetCreated()
    {
        _cli.Daemon.On(
            HttpMethod.Post,
            "/api/instances",
            StubDaemon.Json(HttpStatusCode.Accepted, _provisioning, CliJsonContext.Default.InstanceDto));
    }

    private InstanceDto Ready()
    {
        return _provisioning with
        {
            State = P4ServerInstanceState.Ready,
            RuntimeStatus = InstanceRuntimeStatus.Running,
        };
    }

    private Func<HttpResponseMessage> Operation(
        OperationStatus status,
        OperationStepStatus first,
        OperationStepStatus second,
        string? error = null)
    {
        OperationSnapshot snapshot = new(
            _provisioning.Id,
            "provision",
            status,
            [
                new OperationStepState("resolve-build", "Get the p4d build", first, null),
                new OperationStepState("start-server", "Start the server", second, error),
            ],
            second == OperationStepStatus.Running ? "start-server" : null,
            null,
            error);
        return StubDaemon.Json(HttpStatusCode.OK, snapshot, CliJsonContext.Default.OperationSnapshot);
    }
}
