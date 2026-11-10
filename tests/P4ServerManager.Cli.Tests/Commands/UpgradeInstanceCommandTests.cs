using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class UpgradeInstanceCommandTests
{
    private readonly CliRunner _cli = new();
    private readonly InstanceDto _main = CliRunner.Instance("main");

    public UpgradeInstanceCommandTests()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                [_main],
                CliJsonContext.Default.IReadOnlyListInstanceDto));
        _cli.Daemon.On(
            HttpMethod.Post,
            UpgradePath,
            StubDaemon.Json(
                HttpStatusCode.Accepted,
                _main with { RuntimeStatus = InstanceRuntimeStatus.Upgrading },
                CliJsonContext.Default.InstanceDto));
    }

    private string UpgradePath => $"/api/instances/{_main.Id}/upgrade";

    [Fact]
    public async Task Upgrade_NeedsYesWithoutATerminal()
    {
        CliResult result = await _cli.RunAsync("instance", "upgrade", "main", "--release", "r25.2");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("--yes");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Upgrade_UpgradesNothingWhenTheConfirmationIsDeclined()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.ConfirmAnswer = false;

        CliResult result = await _cli.RunAsync("instance", "upgrade", "main", "--release", "r25.2");

        result.ExitCode.ShouldBe(1);
        _cli.Prompter.Questions.ShouldHaveSingleItem().ShouldContain("from p4d 2025.1/2850123 to r25.2");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Upgrade_SendsTheReleaseAndReturnsWithoutWaitingWhenAsked()
    {
        CliResult result = await _cli.RunAsync("instance", "upgrade", "main", "--release", "r25.2", "--yes", "--no-wait");

        result.ExitCode.ShouldBe(0);
        using JsonDocument request = JsonDocument.Parse(
            _cli.Daemon.RequestsTo(HttpMethod.Post, UpgradePath).Single().Body!);
        request.RootElement.GetProperty("release").GetString().ShouldBe("r25.2");
        result.Output.ShouldContain("instance operation main --follow");
    }

    [Fact]
    public async Task Upgrade_WaitsForTheUpgrade()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}/operation",
            Operation(OperationStatus.Running, OperationStepStatus.Running),
            Operation(OperationStatus.Succeeded, OperationStepStatus.Succeeded));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(
                HttpStatusCode.OK,
                _main with { Release = "r25.2", Version = "2025.2", Build = "2900000" },
                CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync("instance", "upgrade", "main", "--release", "r25.2", "--yes");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.ShouldBe(
        [
            "Upgrading main to r25.2...",
            "[ .. ] Upgrade the database",
            "[ ok ] Upgrade the database",
            "main now runs p4d 2025.2/2900000.",
        ]);
    }

    [Fact]
    public async Task Upgrade_TellsThatTheInstanceWasAlreadyUpToDate()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}/operation",
            Operation(OperationStatus.Succeeded, OperationStepStatus.Skipped, outcome: "up-to-date"));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _main, CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync("instance", "upgrade", "main", "--release", "r25.1", "--yes");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.Last()
            .ShouldBe("main already runs the latest build, p4d 2025.1/2850123. Nothing was changed.");
    }

    [Fact]
    public async Task Upgrade_ReportsTheLastErrorOfTheInstanceWhenTheUpgradeFails()
    {
        const string LastError = "'p4d -xu' failed. The database may be partly upgraded.";
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}/operation",
            Operation(OperationStatus.Failed, OperationStepStatus.Failed, "'p4d -xu' failed."));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _main with { LastError = LastError }, CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync("instance", "upgrade", "main", "--release", "r25.2", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("Error: 'p4d -xu' failed.");
        result.Error.ShouldContain("The database may be partly upgraded.");
    }

    [Fact]
    public async Task Upgrade_ReportsAConflict()
    {
        _cli.Daemon.On(
            HttpMethod.Post,
            UpgradePath,
            StubDaemon.Problem(HttpStatusCode.Conflict, "Instance main runs r25.1: its database cannot go back to r24.2."));

        CliResult result = await _cli.RunAsync("instance", "upgrade", "main", "--release", "r24.2", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("its database cannot go back to r24.2.");
    }

    private Func<HttpResponseMessage> Operation(
        OperationStatus status,
        OperationStepStatus step,
        string? error = null,
        string? outcome = null)
    {
        OperationSnapshot snapshot = new(
            _main.Id,
            "upgrade",
            status,
            [new OperationStepState("upgrade-database", "Upgrade the database", step, error)],
            step == OperationStepStatus.Running ? 0 : null,
            null,
            error,
            outcome ?? (status == OperationStatus.Succeeded ? "upgraded" : null));
        return StubDaemon.Json(HttpStatusCode.OK, snapshot, CliJsonContext.Default.OperationSnapshot);
    }
}
