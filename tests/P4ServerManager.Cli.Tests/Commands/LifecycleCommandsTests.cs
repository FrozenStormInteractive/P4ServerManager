using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class LifecycleCommandsTests
{
    private readonly CliRunner _cli = new();
    private readonly InstanceDto _main = CliRunner.Instance("main", runtimeStatus: InstanceRuntimeStatus.Stopped);
    private readonly InstanceDto _backup = CliRunner.Instance("backup", runtimeStatus: InstanceRuntimeStatus.Stopped);

    public LifecycleCommandsTests()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                [_backup, _main],
                CliJsonContext.Default.IReadOnlyListInstanceDto));
    }

    [Fact]
    public async Task Start_SendsTheNamedInstancesOnce()
    {
        SetResults("start", Result(_backup, InstanceActionOutcome.Succeeded), Result(_main, InstanceActionOutcome.Succeeded));

        CliResult result = await _cli.RunAsync("instance", "start", "main", "BACKUP", "main");

        result.ExitCode.ShouldBe(0);
        InstanceSelectionRequest selection = SentSelection("start");
        selection.InstanceIds.ShouldBe([_main.Id, _backup.Id]);
        selection.All.ShouldBeFalse();
        result.OutputLines.ShouldBe(["Starting main, backup...", "Started backup.", "Started main."]);
    }

    [Fact]
    public async Task Stop_SelectsAllTheInstances()
    {
        SetResults("stop", Result(_backup, InstanceActionOutcome.Skipped), Result(_main, InstanceActionOutcome.Succeeded));

        CliResult result = await _cli.RunAsync("instance", "stop", "--all");

        result.ExitCode.ShouldBe(0);
        InstanceSelectionRequest selection = SentSelection("stop");
        selection.All.ShouldBeTrue();
        selection.InstanceIds.ShouldBeNull();
        _cli.Daemon.RequestsTo(HttpMethod.Get, "/api/instances").ShouldBeEmpty();
        result.OutputLines.ShouldBe(["Stopping all the instances...", "backup is already stopped.", "Stopped main."]);
    }

    [Fact]
    public async Task Restart_FailsWhenOneInstanceFails()
    {
        SetResults(
            "restart",
            Result(_backup, InstanceActionOutcome.Failed, "p4d did not start."),
            Result(_main, InstanceActionOutcome.Succeeded));

        CliResult result = await _cli.RunAsync("instance", "restart", "-a");

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("Restarted main.");
        result.Error.Trim().ShouldBe("Error: backup: p4d did not start.");
    }

    [Theory]
    [InlineData("start")]
    [InlineData("start", "main", "--all")]
    public async Task Start_NeedsEitherNamesOrAll(params string[] args)
    {
        CliResult result = await _cli.RunAsync(["instance", .. args]);

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("--all");
        _cli.Daemon.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_StopsAtAnUnknownName()
    {
        CliResult result = await _cli.RunAsync("instance", "start", "main", "missing");

        result.ExitCode.ShouldBe(1);
        result.Error.Trim().ShouldBe("Error: No instance is named missing.");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Start_WritesTheResultsAsJson()
    {
        SetResults("start", Result(_main, InstanceActionOutcome.Succeeded));

        CliResult result = await _cli.RunAsync("instance", "start", "main", "--json");

        result.ExitCode.ShouldBe(0);
        result.Error.ShouldContain("Starting main...");
        JsonSerializer.Deserialize(result.Output, CliJsonContext.Default.IReadOnlyListInstanceActionResult)
            .ShouldHaveSingleItem()
            .Outcome.ShouldBe(InstanceActionOutcome.Succeeded);
    }

    private void SetResults(string action, params InstanceActionResult[] results)
    {
        _cli.Daemon.On(
            HttpMethod.Post,
            $"/api/instances/{action}",
            StubDaemon.Json<IReadOnlyList<InstanceActionResult>>(
                HttpStatusCode.OK,
                results,
                CliJsonContext.Default.IReadOnlyListInstanceActionResult));
    }

    private InstanceSelectionRequest SentSelection(string action)
    {
        string body = _cli.Daemon.RequestsTo(HttpMethod.Post, $"/api/instances/{action}").ShouldHaveSingleItem().Body!;
        return JsonSerializer.Deserialize(body, CliJsonContext.Default.InstanceSelectionRequest)!;
    }

    private static InstanceActionResult Result(InstanceDto instance, InstanceActionOutcome outcome, string? error = null)
    {
        return new InstanceActionResult(instance, outcome, error);
    }
}
