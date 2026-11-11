using Microsoft.Extensions.Logging.Abstractions;

using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.PortChange;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Tests.Services.Instances;

/// <summary>
/// The update of the address of the server spec, with a fake p4: the commands are only recorded.
/// </summary>
public sealed class UpdateServerAddressStepTests : IDisposable
{
    // What p4 prints with --field Address=...: it changes the field itself.
    private const string NewSpec = "ServerID:\tmain\n\nAddress:\tssl:1777\n\nDescription:\n\tAddress: ssl:1666\n";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly UpdateServerAddressStep _step;

    public UpdateServerAddressStepTests()
    {
        IP4ServerBuildCache buildCache = Substitute.For<IP4ServerBuildCache>();
        buildCache.GetP4Path(Arg.Any<P4ServerBuild>()).Returns("/builds/2850123/p4");
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, string.Empty, string.Empty));
        _processRunner.RunAsync(
                Arg.Is<ProcessCommand>(c => c.Arguments.Contains("--field")),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, NewSpec, string.Empty));
        _step = new UpdateServerAddressStep(
            buildCache,
            _processRunner,
            new DaemonPaths(_directory),
            NullLogger<UpdateServerAddressStep>.Instance);
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "server.id"), "main\n");
    }

    private string RootPath => Path.Combine(_directory, "root");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task LetsP4MoveTheAddressToTheNewPort()
    {
        StubServers("""{"ServerID":"edge","Address":"1666"}""", """{"ServerID":"main","Address":"ssl:1666"}""");

        string? summary = await RunAsync();

        summary.ShouldBe("Address ssl:1666 to ssl:1777");
        GetSpecCommands().ShouldBe(
        [
            "-Mj -ztag servers",
            "--field Address=ssl:1777 server -o main",
            "server -i",
        ]);
        // The daemon saves what p4 printed: it never edits the spec itself.
        GetCommands().Single(c => Describe(c) == "server -i").StandardInput.ShouldBe(NewSpec);
    }

    [Fact]
    public async Task KeepsAnAddressThatDoesNotNameTheOldPort()
    {
        StubServers("""{"ServerID":"main","Address":"p4.example.com:2000"}""");

        string? summary = await RunAsync();

        summary.ShouldBe("Address p4.example.com:2000 kept: it does not name port 1666");
        GetSpecCommands().ShouldBe(["-Mj -ztag servers"]);
    }

    [Fact]
    public async Task LeavesAServerWithoutASpecOrAnAddressAsItIs()
    {
        StubServers("""{"ServerID":"edge","Address":"1666"}""");
        (await RunAsync()).ShouldBe("No server spec main");

        StubServers("""{"ServerID":"main","Type":"server"}""");
        (await RunAsync()).ShouldBe("Spec main has no address");

        GetSpecCommands().ShouldAllBe(command => command == "-Mj -ztag servers");
    }

    [Fact]
    public async Task FindsTheSpecOfACaseInsensitiveServerWhateverItsCase()
    {
        StubServers("""{"ServerID":"Main","Address":"1666"}""");

        string? summary = await RunAsync(caseSensitive: false);

        summary.ShouldBe("Address 1666 to 1777");
        GetSpecCommands().ShouldContain("--field Address=1777 server -o Main");
    }

    [Fact]
    public async Task TellsWhyP4RefusedTheList()
    {
        _processRunner.RunAsync(
                Arg.Is<ProcessCommand>(c => c.Arguments.Contains("servers")),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(
                1,
                """{"code0":1,"data":"You don't have permission for this operation.\n","severity":3}""" + "\n",
                string.Empty));

        InstanceOperationException exception = await Should.ThrowAsync<InstanceOperationException>(() => RunAsync());

        exception.Message.ShouldEndWith("failed: You don't have permission for this operation.");
        GetSpecCommands().ShouldBe(["-Mj -ztag servers"]);
    }

    private void StubServers(params string[] records)
    {
        _processRunner.RunAsync(
                Arg.Is<ProcessCommand>(c => c.Arguments.Contains("servers")),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, string.Join('\n', records) + "\n", string.Empty));
    }

    private Task<string?> RunAsync(bool caseSensitive = true)
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
            RootPath = RootPath,
            Release = "r25.1",
            CaseSensitive = caseSensitive,
            Build = build,
            BuildId = build.Id,
        };
        PortChangeContext context = new(
            null!,
            instance,
            wasRunning: true,
            newPort: 1777,
            new SuperUserCredentials("admin", "secret"));
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
