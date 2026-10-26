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
        _cli.Daemon.On(HttpMethod.Delete, DeletePath(false), StubDaemon.Status(HttpStatusCode.NoContent));
        _cli.Daemon.On(HttpMethod.Delete, DeletePath(true), StubDaemon.Status(HttpStatusCode.NoContent));
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

    private string DeletePath(bool deleteFiles)
    {
        return $"/api/instances/{_main.Id}?deleteFiles={(deleteFiles ? "true" : "false")}";
    }
}
