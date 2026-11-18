using System.Net;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class DeleteInstanceCommandTests
{
    private readonly CliRunner _cli = new();
    private readonly InstanceDto _main = CliRunner.Instance("main");

    public DeleteInstanceCommandTests()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(
                HttpStatusCode.OK,
                [_main],
                CliJsonContext.Default.IReadOnlyListInstanceDto));
        _cli.Daemon.On(HttpMethod.Delete, DeletePath(false), Deleting());
        _cli.Daemon.On(HttpMethod.Delete, DeletePath(true), Deleting());
        // Gone once the deletion has begun, unless a test says otherwise.
        _cli.Daemon.On(HttpMethod.Get, InstancePath, StubDaemon.Problem(HttpStatusCode.NotFound, "Not found."));
    }

    [Fact]
    public async Task Delete_NeedsYesWithoutATerminal()
    {
        CliResult result = await _cli.RunAsync("instance", "delete", "main");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("--yes");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Delete_KeepsTheInstanceWhenTheConfirmationIsDeclined()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.ConfirmAnswer = false;

        CliResult result = await _cli.RunAsync("instance", "delete", "main");

        result.ExitCode.ShouldBe(1);
        _cli.Prompter.Questions.ShouldHaveSingleItem().ShouldContain("main");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Delete_DeletesOnceConfirmed()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.ConfirmAnswer = true;

        CliResult result = await _cli.RunAsync("instance", "rm", "main");

        result.ExitCode.ShouldBe(0);
        _cli.Daemon.RequestsTo(HttpMethod.Delete, DeletePath(false)).ShouldHaveSingleItem();
        result.Output.ShouldContain("Deleted main.");
    }

    [Fact]
    public async Task Delete_PassesTheDeletionOfTheFiles()
    {
        CliResult result = await _cli.RunAsync("instance", "delete", "main", "--yes", "--delete-files");

        result.ExitCode.ShouldBe(0);
        _cli.Prompter.Questions.ShouldBeEmpty();
        _cli.Daemon.RequestsTo(HttpMethod.Delete, DeletePath(true)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Delete_SaysWhatGoesWithTheFilesInTheConfirmation()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.ConfirmAnswer = true;

        CliResult result = await _cli.RunAsync("instance", "delete", "main", "--delete-files");

        result.ExitCode.ShouldBe(0, result.Error);
        _cli.Prompter.Questions.ShouldHaveSingleItem()
            .ShouldBe("Delete the instance main and the files of its server: its root, depots, checkpoints, journals and logs?");
        _cli.Daemon.Requests.ShouldNotContain(r => r.PathAndQuery.Contains("/files", StringComparison.Ordinal));
        _cli.Daemon.RequestsTo(HttpMethod.Delete, DeletePath(true)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Delete_ReportsWhyTheDaemonRefusesToDeleteTheFiles()
    {
        _cli.Daemon.On(
            HttpMethod.Delete,
            DeletePath(true),
            StubDaemon.Problem(
                HttpStatusCode.Conflict,
                "The files of instance main cannot be deleted. /volume1 is a root of the file system."));

        CliResult result = await _cli.RunAsync("instance", "delete", "main", "--yes", "--delete-files");

        result.ExitCode.ShouldBe(1);
        result.Error.Trim()
            .ShouldBe("Error: The files of instance main cannot be deleted. /volume1 is a root of the file system.");
    }

    [Fact]
    public async Task Delete_WaitsForTheEndOfTheDeletion()
    {
        InstanceDto deleting = _main with { RuntimeStatus = InstanceRuntimeStatus.Deleting };
        _cli.Daemon.On(
            HttpMethod.Get,
            InstancePath,
            StubDaemon.Json(HttpStatusCode.OK, deleting, CliJsonContext.Default.InstanceDto),
            StubDaemon.Json(HttpStatusCode.OK, deleting, CliJsonContext.Default.InstanceDto),
            StubDaemon.Problem(HttpStatusCode.NotFound, "Not found."));

        CliResult result = await _cli.RunAsync("instance", "delete", "main", "--yes", "--delete-files");

        result.ExitCode.ShouldBe(0, result.Error);
        result.Output.ShouldContain("Deleted main.");
        _cli.Daemon.RequestsTo(HttpMethod.Get, InstancePath).Count().ShouldBe(3);
    }

    [Fact]
    public async Task Delete_ReportsTheErrorTheFailedDeletionLeft()
    {
        InstanceDto failed = _main with
        {
            RuntimeStatus = InstanceRuntimeStatus.Stopped,
            LastError = "The files of instance main could not be deleted: Access denied.",
        };
        _cli.Daemon.On(
            HttpMethod.Get,
            InstancePath,
            StubDaemon.Json(HttpStatusCode.OK, failed, CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync("instance", "delete", "main", "--yes", "--delete-files");

        result.ExitCode.ShouldBe(1);
        result.Error.Trim().ShouldBe("Error: The files of instance main could not be deleted: Access denied.");
        result.Output.ShouldNotContain("Deleted main.");
    }

    [Fact]
    public async Task Delete_WithoutWaiting_ReturnsOnceTheDeletionHasBegun()
    {
        CliResult result = await _cli.RunAsync("instance", "delete", "main", "--yes", "--no-wait");

        result.ExitCode.ShouldBe(0, result.Error);
        result.Output.ShouldContain("Deleting main. The deletion goes on in the daemon");
        _cli.Daemon.RequestsTo(HttpMethod.Get, InstancePath).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_ReportsAConflict()
    {
        _cli.Daemon.On(
            HttpMethod.Delete,
            DeletePath(false),
            StubDaemon.Problem(HttpStatusCode.Conflict, "Instance main is being provisioned."));

        CliResult result = await _cli.RunAsync("instance", "delete", "main", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.Trim().ShouldBe("Error: Instance main is being provisioned.");
    }

    private string InstancePath => $"/api/instances/{_main.Id}";

    private Func<HttpResponseMessage> Deleting()
    {
        return StubDaemon.Json(
            HttpStatusCode.Accepted,
            _main with { RuntimeStatus = InstanceRuntimeStatus.Deleting },
            CliJsonContext.Default.InstanceDto);
    }

    private string DeletePath(bool deleteFiles)
    {
        return $"/api/instances/{_main.Id}?deleteFiles={(deleteFiles ? "true" : "false")}";
    }
}
