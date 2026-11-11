using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class ChangePortCommandTests
{
    private readonly CliRunner _cli = new();
    private readonly InstanceDto _main = CliRunner.Instance("main");

    public ChangePortCommandTests()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                [_main],
                CliJsonContext.Default.IReadOnlyListInstanceDto));
        _cli.Daemon.On(
            HttpMethod.Put,
            PortPath,
            StubDaemon.Json(
                HttpStatusCode.Accepted,
                _main with { RuntimeStatus = InstanceRuntimeStatus.ChangingPort },
                CliJsonContext.Default.InstanceDto));
    }

    private string PortPath => $"/api/instances/{_main.Id}/port";

    [Fact]
    public async Task Port_NeedsYesWithoutATerminal()
    {
        CliResult result = await _cli.RunAsync("instance", "port", "main", "1777");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("--yes");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Port_ChangesNothingWhenTheConfirmationIsDeclined()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.ConfirmAnswer = false;

        CliResult result = await _cli.RunAsync("instance", "port", "main", "1777");

        result.ExitCode.ShouldBe(1);
        _cli.Prompter.Questions.ShouldHaveSingleItem().ShouldContain("from port 1666 to 1777");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Port_SendsTheNewPortAndReturnsWithoutWaitingWhenAsked()
    {
        CliResult result = await _cli.RunAsync("instance", "port", "main", "1777", "--yes", "--no-wait");

        result.ExitCode.ShouldBe(0);
        using JsonDocument body = JsonDocument.Parse(_cli.Daemon.RequestsTo(HttpMethod.Put, PortPath).Single().Body!);
        body.RootElement.GetProperty("port").GetInt32().ShouldBe(1777);
        result.Output.ShouldContain("instance operation main --follow");
    }

    [Fact]
    public async Task Port_WaitsForTheChange()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}/operation",
            Operation(OperationStatus.Running, OperationStepStatus.Running),
            Operation(OperationStatus.Succeeded, OperationStepStatus.Succeeded));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _main with { Port = 1777 }, CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync("instance", "port", "main", "1777", "--yes");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.ShouldBe(
        [
            "Moving main to port 1777...",
            "[ .. ] Change the port",
            "[ ok ] Change the port",
            "main now listens on port 1777.",
        ]);
    }

    [Fact]
    public async Task Port_ReportsAConflict()
    {
        _cli.Daemon.On(
            HttpMethod.Put,
            PortPath,
            StubDaemon.Problem(HttpStatusCode.Conflict, "Instance main already listens on port 1666."));

        CliResult result = await _cli.RunAsync("instance", "port", "main", "1666", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("already listens on port 1666.");
    }

    private Func<HttpResponseMessage> Operation(OperationStatus status, OperationStepStatus step)
    {
        OperationSnapshot snapshot = new(
            _main.Id,
            "change-port",
            status,
            [new OperationStepState("set-port", "Change the port", step, null)],
            step == OperationStepStatus.Running ? 0 : null,
            null,
            null);
        return StubDaemon.Json(HttpStatusCode.OK, snapshot, CliJsonContext.Default.OperationSnapshot);
    }
}
