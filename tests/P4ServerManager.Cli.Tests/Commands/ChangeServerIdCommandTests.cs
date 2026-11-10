using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class ChangeServerIdCommandTests
{
    private readonly CliRunner _cli = new();
    private readonly InstanceDto _main = CliRunner.Instance("main");

    public ChangeServerIdCommandTests()
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
            ServerIdPath,
            StubDaemon.Json(
                HttpStatusCode.Accepted,
                _main with { RuntimeStatus = InstanceRuntimeStatus.ChangingServerId },
                CliJsonContext.Default.InstanceDto));
    }

    private string ServerIdPath => $"/api/instances/{_main.Id}/server-id";

    [Fact]
    public async Task ServerId_NeedsYesWithoutATerminal()
    {
        CliResult result = await _cli.RunAsync("instance", "server-id", "main", "edge");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("--yes");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ServerId_ChangesNothingWhenTheConfirmationIsDeclined()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.ConfirmAnswer = false;

        CliResult result = await _cli.RunAsync("instance", "server-id", "main", "edge");

        result.ExitCode.ShouldBe(1);
        _cli.Prompter.Questions.ShouldHaveSingleItem().ShouldContain("from main to edge");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ServerId_SendsTheNewServerIdAndReturnsWithoutWaitingWhenAsked()
    {
        CliResult result = await _cli.RunAsync("instance", "server-id", "main", "edge", "--yes", "--no-wait");

        result.ExitCode.ShouldBe(0);
        using JsonDocument body = JsonDocument.Parse(_cli.Daemon.RequestsTo(HttpMethod.Put, ServerIdPath).Single().Body!);
        body.RootElement.GetProperty("serverId").GetString().ShouldBe("edge");
        result.Output.ShouldContain("instance operation main --follow");
    }

    [Fact]
    public async Task ServerId_WaitsForTheChange()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}/operation",
            Operation(OperationStatus.Running, OperationStepStatus.Running),
            Operation(OperationStatus.Succeeded, OperationStepStatus.Succeeded));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _main with { ServerId = "edge" }, CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync("instance", "server-id", "main", "edge", "--yes");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.ShouldBe(
        [
            "Changing the server ID of main to edge...",
            "[ .. ] Set the server ID",
            "[ ok ] Set the server ID",
            "main now has server ID edge.",
        ]);
    }

    [Fact]
    public async Task ServerId_ReportsAConflict()
    {
        _cli.Daemon.On(
            HttpMethod.Put,
            ServerIdPath,
            StubDaemon.Problem(HttpStatusCode.Conflict, "Instance main already has server ID main."));

        CliResult result = await _cli.RunAsync("instance", "server-id", "main", "main", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("already has server ID main.");
    }

    private Func<HttpResponseMessage> Operation(OperationStatus status, OperationStepStatus step)
    {
        OperationSnapshot snapshot = new(
            _main.Id,
            "change-server-id",
            status,
            [new OperationStepState("set-server-id", "Set the server ID", step, null)],
            step == OperationStepStatus.Running ? 0 : null,
            null,
            null);
        return StubDaemon.Json(HttpStatusCode.OK, snapshot, CliJsonContext.Default.OperationSnapshot);
    }
}
