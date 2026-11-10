using Microsoft.Extensions.Logging.Abstractions;

using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Tests.Services.Instances;

/// <summary>
/// The renaming of the server spec, with a fake p4: the commands are only recorded.
/// </summary>
public sealed class RenameServerSpecStepTests
{
    private const string OldSpec = "# A Perforce Server Specification.\n\nServerID:\tmain\n\nType:\tserver\n\n"
        + "Services:\tcommit-server\n\nAddress:\t1666\n\nDescription:\n\tOur server.\n";

    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly RenameServerSpecStep _step;

    public RenameServerSpecStepTests()
    {
        IP4ServerBuildCache buildCache = Substitute.For<IP4ServerBuildCache>();
        buildCache.GetP4Path(Arg.Any<P4ServerBuild>()).Returns("/builds/2850123/p4");
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, string.Empty, string.Empty));
        _processRunner.RunAsync(
                Arg.Is<ProcessCommand>(c => c.Arguments.Contains("-o")),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, OldSpec, string.Empty));
        _step = new RenameServerSpecStep(
            buildCache,
            _processRunner,
            new DaemonPaths(Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"))),
            NullLogger<RenameServerSpecStep>.Instance);
    }

    [Fact]
    public async Task DeletesTheOldSpecThenSavesItUnderTheNewServerId()
    {
        StubServers("main");

        string? summary = await RunAsync("main", "edge");

        // p4d allows one commit server only: the old spec goes first.
        summary.ShouldBe("Spec main renamed to edge");
        GetSpecCommands().ShouldBe(
        [
            "-ztag servers",
            "server -o main",
            "server -d main",
            "server -i",
        ]);
        string spec = GetCommand("server -i").StandardInput!;
        spec.ShouldContain("ServerID:\tedge\n");
        spec.ShouldContain("\tOur server.\n");
    }

    [Fact]
    public async Task SavesTheOldSpecAgainWhenP4dRefusesTheNewOne()
    {
        StubServers("main");
        _processRunner.RunAsync(
                Arg.Is<ProcessCommand>(c => c.Arguments.Last() == "-i" && c.StandardInput!.Contains("ServerID:\tedge")),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(1, string.Empty, "Error in server specification."));

        await Should.ThrowAsync<InstanceOperationException>(() => RunAsync("main", "edge"));

        GetSpecCommands().ShouldBe(
        [
            "-ztag servers",
            "server -o main",
            "server -d main",
            "server -i",
            "server -i",
        ]);
        GetCommands().Last(c => Describe(c) == "server -i").StandardInput.ShouldBe(OldSpec);
    }

    [Fact]
    public async Task CreatesTheRecommendedSpecWhenTheServerHasNone()
    {
        StubServers();

        string? summary = await RunAsync("main", "edge");

        summary.ShouldBe("Spec edge created");
        GetSpecCommands().ShouldBe(["-ztag servers", "server -i"]);
        string spec = GetCommand("server -i").StandardInput!;
        spec.ShouldStartWith("ServerID:\tedge\n");
        spec.ShouldContain("Services:\tcommit-server\n");
    }

    [Fact]
    public async Task KeepsASpecOfTheNewServerIdThatExistsAlready()
    {
        StubServers("main", "edge");

        string? summary = await RunAsync("main", "edge");

        summary.ShouldBe("Spec edge kept, spec main deleted");
        GetSpecCommands().ShouldBe(["-ztag servers", "server -d main"]);
    }

    [Fact]
    public async Task OnlyChangesTheCaseOfTheSpecOfACaseInsensitiveServer()
    {
        StubServers("main");

        string? summary = await RunAsync("main", "Main", caseSensitive: false);

        // Deleting "main" would delete the spec just saved as "Main".
        summary.ShouldBe("Spec main renamed to Main");
        GetSpecCommands().ShouldBe(["-ztag servers", "server -o main", "server -i"]);
    }

    private void StubServers(params string[] serverIds)
    {
        string output = string.Concat(serverIds.Select(id => $"... ServerID {id}\n... Type server\n\n"));
        _processRunner.RunAsync(
                Arg.Is<ProcessCommand>(c => c.Arguments.Contains("servers")),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, output, string.Empty));
    }

    private Task<string?> RunAsync(string oldServerId, string newServerId, bool caseSensitive = true)
    {
        P4ServerBuild build = new()
        {
            Id = Guid.NewGuid(),
            Release = "r25.1",
            Version = "2025.1",
            BuildNumber = "2850123",
            Platform = "linux-x64",
            Sha256 = string.Empty,
        };
        P4ServerInstance instance = new()
        {
            Id = Guid.NewGuid(),
            Name = "main",
            Port = 1666,
            RootPath = "/srv/main/root",
            Release = "r25.1",
            CaseSensitive = caseSensitive,
            Build = build,
            BuildId = build.Id,
        };
        ServerIdAssignmentContext context = new(
            null!,
            instance,
            wasRunning: true,
            newServerId,
            new SuperUserCredentials("admin", "secret"))
        {
            OldServerId = oldServerId,
        };
        return _step.RunAsync(context, new Progress<OperationStepProgress>(), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The p4 commands, without the connection options, and without the login and the logout around them.
    /// </summary>
    private List<string> GetSpecCommands()
    {
        return GetCommands()
            .Select(Describe)
            .Where(command => command is not ("login" or "logout"))
            .ToList();
    }

    private ProcessCommand GetCommand(string description)
    {
        return GetCommands().Single(c => Describe(c) == description);
    }

    private IEnumerable<ProcessCommand> GetCommands()
    {
        return _processRunner.ReceivedCalls().Select(call => (ProcessCommand)call.GetArguments()[0]!);
    }

    private static string Describe(ProcessCommand command)
    {
        // -p, -u and -C, each with its value.
        return string.Join(' ', command.Arguments.Skip(6));
    }
}
