using System.Net;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class ConvertInstanceToUnicodeCommandTests
{
    private readonly CliRunner _cli = new();
    private readonly InstanceDto _main = CliRunner.Instance("main");

    public ConvertInstanceToUnicodeCommandTests()
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
            ConvertPath,
            StubDaemon.Json(
                HttpStatusCode.Accepted,
                _main with { RuntimeStatus = InstanceRuntimeStatus.ConvertingToUnicode },
                CliJsonContext.Default.InstanceDto));
    }

    private string ConvertPath => $"/api/instances/{_main.Id}/convert-to-unicode";

    [Fact]
    public async Task Convert_NeedsYesWithoutATerminal()
    {
        CliResult result = await _cli.RunAsync("instance", "convert-to-unicode", "main");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("--yes");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Convert_ConvertsNothingWhenTheConfirmationIsDeclined()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.ConfirmAnswer = false;

        CliResult result = await _cli.RunAsync("instance", "convert-to-unicode", "main");

        result.ExitCode.ShouldBe(1);
        _cli.Prompter.Questions.ShouldHaveSingleItem().ShouldContain("it cannot be undone");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Convert_ReturnsWithoutWaitingWhenAsked()
    {
        CliResult result = await _cli.RunAsync("instance", "convert-to-unicode", "main", "--yes", "--no-wait");

        result.ExitCode.ShouldBe(0);
        _cli.Daemon.RequestsTo(HttpMethod.Post, ConvertPath).ShouldHaveSingleItem();
        result.Output.ShouldContain("instance operation main --follow");
    }

    [Fact]
    public async Task Convert_WaitsForTheConversion()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}/operation",
            Operation(OperationStatus.Running, OperationStepStatus.Running),
            Operation(OperationStatus.Succeeded, OperationStepStatus.Succeeded));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _main with { Unicode = true }, CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync("instance", "convert-to-unicode", "main", "--yes");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.ShouldBe(
        [
            "Converting main to Unicode mode...",
            "[ .. ] Switch to Unicode mode",
            "[ ok ] Switch to Unicode mode",
            "main now runs in Unicode mode.",
        ]);
    }

    [Fact]
    public async Task Convert_ReportsWhyP4dRefusedTheConversion()
    {
        const string Error = "'p4d -xi' failed: Table db.job has 7 rows with invalid UTF8.";
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}/operation",
            Operation(OperationStatus.Failed, OperationStepStatus.Failed, Error));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _main with { LastError = Error }, CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync("instance", "convert-to-unicode", "main", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("Table db.job has 7 rows with invalid UTF8.");
    }

    [Fact]
    public async Task Convert_ReportsAConflict()
    {
        _cli.Daemon.On(
            HttpMethod.Post,
            ConvertPath,
            StubDaemon.Problem(HttpStatusCode.Conflict, "Instance main already runs in Unicode mode."));

        CliResult result = await _cli.RunAsync("instance", "convert-to-unicode", "main", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("already runs in Unicode mode.");
    }

    private Func<HttpResponseMessage> Operation(OperationStatus status, OperationStepStatus step, string? error = null)
    {
        OperationSnapshot snapshot = new(
            _main.Id,
            "convert-to-unicode",
            status,
            [new OperationStepState("switch-to-unicode", "Switch to Unicode mode", step, error)],
            step == OperationStepStatus.Running ? 0 : null,
            null,
            error);
        return StubDaemon.Json(HttpStatusCode.OK, snapshot, CliJsonContext.Default.OperationSnapshot);
    }
}
