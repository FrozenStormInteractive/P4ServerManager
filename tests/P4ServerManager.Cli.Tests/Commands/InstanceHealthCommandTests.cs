using System.Net;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class InstanceHealthCommandTests
{
    private readonly CliRunner _cli = new();
    private readonly InstanceDto _main = CliRunner.Instance("main");
    private readonly Guid _runId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    public InstanceHealthCommandTests()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                [_main],
                CliJsonContext.Default.IReadOnlyListInstanceDto));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _main, CliJsonContext.Default.InstanceDto));
    }

    private string ChecksPath => $"/api/instances/{_main.Id}/health-checks";

    [Fact]
    public async Task Check_RunsBothChecksAndWaitsForAHealthyEnd()
    {
        _cli.Daemon.On(HttpMethod.Post, ChecksPath, Reply(Run(HealthCheckStatus.Running), HttpStatusCode.Accepted));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"{ChecksPath}/{_runId}",
            Reply(Run(HealthCheckStatus.Running) with
            {
                Progress = new HealthCheckProgressDto(HealthCheckKind.Validate, "db.rev", 1, 25),
            }),
            Reply(Run(HealthCheckStatus.Healthy)));

        CliResult result = await _cli.RunAsync("instance", "health", "check", "main", "--yes");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.ShouldBe(
        [
            "Checking main...",
            "[ 25%] db.rev",
            "Healthy: no problems found.",
        ]);
        _cli.Daemon.RequestsTo(HttpMethod.Post, ChecksPath).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Check_NeedsYesWithoutATerminalWhenItStopsTheServer()
    {
        CliResult result = await _cli.RunAsync("instance", "health", "check", "main");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("--yes");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Check_ChecksNothingWhenTheConfirmationIsDeclined()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.ConfirmAnswer = false;

        CliResult result = await _cli.RunAsync("instance", "health", "check", "main");

        result.ExitCode.ShouldBe(1);
        _cli.Prompter.Questions.ShouldHaveSingleItem().ShouldContain("disconnects its users");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Check_FailsWhenTheServerDoesNotStartAgain()
    {
        _cli.Daemon.On(HttpMethod.Post, ChecksPath, Reply(Run(HealthCheckStatus.Running), HttpStatusCode.Accepted));
        _cli.Daemon.On(HttpMethod.Get, $"{ChecksPath}/{_runId}", Reply(Run(HealthCheckStatus.Healthy)));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(
                HttpStatusCode.OK,
                _main with { RuntimeStatus = InstanceRuntimeStatus.CheckingHealth },
                CliJsonContext.Default.InstanceDto),
            StubDaemon.Json(
                HttpStatusCode.OK,
                _main with { RuntimeStatus = InstanceRuntimeStatus.Stopped, LastError = "The port is in use." },
                CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync("instance", "health", "check", "main", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("The port is in use.");
    }

    [Fact]
    public async Task Check_ExitsWithAnErrorWhenItFindsProblems()
    {
        HealthCheckRunDto found = Run(HealthCheckStatus.ProblemsFound) with
        {
            Findings =
            [
                new HealthCheckFindingDto(HealthCheckKind.Validate, "db.rev", "**** Checksum mismatch ****"),
                new HealthCheckFindingDto(
                    HealthCheckKind.CrossCheck,
                    "db.change/db.desc",
                    "db.change/db.desc inconsistencies found."),
            ],
        };
        _cli.Daemon.On(HttpMethod.Post, ChecksPath, Reply(Run(HealthCheckStatus.Running), HttpStatusCode.Accepted));
        _cli.Daemon.On(HttpMethod.Get, $"{ChecksPath}/{_runId}", Reply(found));

        CliResult result = await _cli.RunAsync("instance", "health", "check", "main", "--yes");

        result.ExitCode.ShouldBe(1);
        result.OutputLines.ShouldContain("[problem] db.rev: **** Checksum mismatch ****");
        result.OutputLines.ShouldContain("[problem] db.change/db.desc inconsistencies found.");
        result.Output.ShouldContain("Problems found: 2.");
    }

    [Fact]
    public async Task Check_ReturnsWithoutWaitingWhenAsked()
    {
        _cli.Daemon.On(HttpMethod.Post, ChecksPath, Reply(Run(HealthCheckStatus.Running), HttpStatusCode.Accepted));

        CliResult result = await _cli.RunAsync("instance", "health", "check", "main", "--only", "cross-check", "--no-wait", "--yes");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("instance health show main");
        _cli.Daemon.RequestsTo(HttpMethod.Get, $"{ChecksPath}/{_runId}").ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_ReportsAConflict()
    {
        _cli.Daemon.On(
            HttpMethod.Post,
            ChecksPath,
            StubDaemon.Problem(HttpStatusCode.Conflict, "A health check already runs on instance main."));

        CliResult result = await _cli.RunAsync("instance", "health", "check", "main", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("already runs");
    }

    [Fact]
    public async Task List_ShowsTheReports()
    {
        _cli.Daemon.On(HttpMethod.Get, ChecksPath, ReplyList(Run(HealthCheckStatus.Healthy)));

        CliResult result = await _cli.RunAsync("instance", "health", "list", "main");

        result.ExitCode.ShouldBe(0);
        result.OutputLines[0].ShouldStartWith("ID");
        result.OutputLines[1].ShouldStartWith("7c9e6679");
        result.OutputLines[1].ShouldContain("Healthy");
        result.OutputLines[1].ShouldContain("validate, cross-check");
    }

    [Fact]
    public async Task List_LeavesOutTheDeletedReports()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            ChecksPath,
            ReplyList(Run(HealthCheckStatus.Healthy) with { ReportHidden = true }));

        CliResult result = await _cli.RunAsync("instance", "health", "list", "main");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("No reports.");
    }

    [Fact]
    public async Task Show_DefaultsToTheLatestReport()
    {
        _cli.Daemon.On(HttpMethod.Get, ChecksPath, ReplyList(Run(HealthCheckStatus.Healthy)));

        CliResult result = await _cli.RunAsync("instance", "health", "show", "main");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Healthy");
    }

    [Fact]
    public async Task Cancel_StopsTheRunningCheck()
    {
        _cli.Daemon.On(HttpMethod.Get, ChecksPath, ReplyList(Run(HealthCheckStatus.Running)));
        _cli.Daemon.On(
            HttpMethod.Post,
            $"{ChecksPath}/{_runId}/cancel",
            () => new HttpResponseMessage(HttpStatusCode.Accepted));

        CliResult result = await _cli.RunAsync("instance", "health", "cancel", "main");

        result.ExitCode.ShouldBe(0);
        _cli.Daemon.RequestsTo(HttpMethod.Post, $"{ChecksPath}/{_runId}/cancel").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Delete_FindsTheReportByTheStartOfItsIdentifier()
    {
        _cli.Daemon.On(HttpMethod.Get, ChecksPath, ReplyList(Run(HealthCheckStatus.Healthy)));
        _cli.Daemon.On(
            HttpMethod.Delete,
            $"{ChecksPath}/{_runId}",
            () => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliResult result = await _cli.RunAsync("instance", "health", "delete", "main", "7c9e6679", "--yes");

        result.ExitCode.ShouldBe(0);
        _cli.Daemon.RequestsTo(HttpMethod.Delete, $"{ChecksPath}/{_runId}").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Delete_NeedsYesWithoutATerminal()
    {
        _cli.Daemon.On(HttpMethod.Get, ChecksPath, ReplyList(Run(HealthCheckStatus.Healthy)));

        CliResult result = await _cli.RunAsync("instance", "health", "delete", "main", "7c9e6679");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("--yes");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Delete);
    }

    private HealthCheckRunDto Run(HealthCheckStatus status)
    {
        return new HealthCheckRunDto(
            _runId,
            _main.Id,
            status,
            [HealthCheckKind.Validate, HealthCheckKind.CrossCheck],
            "2026.1/3089746",
            DateTimeOffset.UnixEpoch,
            status == HealthCheckStatus.Running ? null : DateTimeOffset.UnixEpoch,
            null,
            false,
            null,
            []);
    }

    private static Func<HttpResponseMessage> Reply(HealthCheckRunDto run, HttpStatusCode status = HttpStatusCode.OK)
    {
        return StubDaemon.Json(status, run, CliJsonContext.Default.HealthCheckRunDto);
    }

    private static Func<HttpResponseMessage> ReplyList(params HealthCheckRunDto[] runs)
    {
        return StubDaemon.Json<IReadOnlyList<HealthCheckRunDto>>(
            HttpStatusCode.OK,
            runs,
            CliJsonContext.Default.IReadOnlyListHealthCheckRunDto);
    }
}
