using System.Net;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class BuildCommandsTests : IDisposable
{
    private readonly CliRunner _cli = new();
    private readonly BuildDto _used = Build("2850123", archiveSize: 12_900_000, instances: ["main"]);
    private readonly BuildDto _old = Build("2800000", archiveSize: 900, source: BuildSource.Imported);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "p4sm-cli-tests", Guid.NewGuid().ToString("N"));

    public BuildCommandsTests()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/p4d/builds",
            StubDaemon.Json<IReadOnlyList<BuildDto>>(
                HttpStatusCode.OK,
                [_used, _old],
                CliJsonContext.Default.IReadOnlyListBuildDto));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task List_WritesATable()
    {
        CliResult result = await _cli.RunAsync("build", "list");

        result.ExitCode.ShouldBe(0);
        result.OutputLines.ShouldBe(
        [
            "RELEASE  VERSION         SOURCE    ARCHIVE  EXTRACTED  PROTECTED  USED BY",
            "r25.1    2025.1/2850123  Ftp       12.3 MB  yes        no         main",
            "r25.1    2025.1/2800000  Imported  900 B    no         no         -",
        ]);
    }

    [Fact]
    public async Task Import_SendsP4dAndP4()
    {
        string p4d = WriteFile("p4d");
        string p4 = WriteFile("p4");
        _cli.Daemon.On(
            HttpMethod.Post,
            "/api/p4d/builds/import",
            StubDaemon.Json(HttpStatusCode.Created, _old, CliJsonContext.Default.BuildDto));

        CliResult result = await _cli.RunAsync("build", "import", p4d, p4);

        result.ExitCode.ShouldBe(0);
        string body = _cli.Daemon.RequestsTo(HttpMethod.Post, "/api/p4d/builds/import").ShouldHaveSingleItem().Body!;
        body.ShouldContain("name=p4d");
        body.ShouldContain("name=p4;");
        result.Output.ShouldContain("Imported p4d 2025.1/2800000 of r25.1 (900 B compressed).");
    }

    [Fact]
    public async Task Import_SendsAPackage()
    {
        string package = WriteFile("helix-core-server.tgz");
        _cli.Daemon.On(
            HttpMethod.Post,
            "/api/p4d/builds/import",
            StubDaemon.Json(HttpStatusCode.Created, _old, CliJsonContext.Default.BuildDto));

        CliResult result = await _cli.RunAsync("build", "import", "--package", package);

        result.ExitCode.ShouldBe(0);
        _cli.Daemon.RequestsTo(HttpMethod.Post, "/api/p4d/builds/import").ShouldHaveSingleItem().Body!
            .ShouldContain("name=package");
    }

    [Fact]
    public async Task Import_NeedsP4dAndP4OrAPackage()
    {
        CliResult result = await _cli.RunAsync("build", "import", WriteFile("p4d"));

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("Give p4d and p4, or --package alone.");
        _cli.Daemon.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Download_QueuesTheRelease()
    {
        _cli.Daemon.On(HttpMethod.Post, "/api/p4d/builds/download", StubDaemon.Status(HttpStatusCode.Accepted));

        CliResult result = await _cli.RunAsync("build", "download", "r25.2");

        result.ExitCode.ShouldBe(0);
        _cli.Daemon.RequestsTo(HttpMethod.Post, "/api/p4d/builds/download").ShouldHaveSingleItem().Body!
            .ShouldContain("\"release\": \"r25.2\"");
    }

    [Fact]
    public async Task Protect_FindsTheBuildByItsNumber()
    {
        _cli.Daemon.On(
            HttpMethod.Patch,
            $"/api/p4d/builds/{_old.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _old with { Protected = true }, CliJsonContext.Default.BuildDto));

        CliResult result = await _cli.RunAsync("build", "protect", "2800000");

        result.ExitCode.ShouldBe(0);
        _cli.Daemon.RequestsTo(HttpMethod.Patch, $"/api/p4d/builds/{_old.Id}").ShouldHaveSingleItem().Body!
            .ShouldContain("\"protected\": true");
        result.Output.ShouldContain("p4d 2025.1/2800000 is protected.");
    }

    [Fact]
    public async Task Delete_TellsWhyABuildInUseIsKept()
    {
        _cli.Daemon.On(
            HttpMethod.Delete,
            $"/api/p4d/builds/{_used.Id}",
            StubDaemon.Problem(HttpStatusCode.Conflict, "p4d 2025.1/2850123 is used by main."));

        CliResult result = await _cli.RunAsync("build", "delete", "2025.1/2850123", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("p4d 2025.1/2850123 is used by main.");
    }

    [Fact]
    public async Task Delete_FailsForAnUnknownBuild()
    {
        CliResult result = await _cli.RunAsync("build", "delete", "1234", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("No build of the archive matches 1234.");
    }

    [Fact]
    public async Task Status_ShowsThePolicyAndTheLastErrors()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/p4d/archive/status",
            StubDaemon.Json(
                HttpStatusCode.OK,
                new BuildArchiveStatusDto(
                    true,
                    "Checking r24.2",
                    null,
                    null,
                    "r24.2: not found",
                    null,
                    null,
                    true,
                    [
                        new BuildFetchDto("r25.1", BuildFetchState.Downloading, "p4d", null, 42),
                        new BuildFetchDto("r25.2", BuildFetchState.Queued, null, null, null),
                    ]),
                CliJsonContext.Default.BuildArchiveStatusDto));
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/p4d/archive/policy",
            StubDaemon.Json(
                HttpStatusCode.OK,
                new BuildArchivePolicyDto { AutoDownload = true, CheckIntervalHours = 6, Releases = ["r25.1", "r24.2"] },
                CliJsonContext.Default.BuildArchivePolicyDto));

        CliResult result = await _cli.RunAsync("build", "status");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Automatic download: every 6 h");
        result.Output.ShouldContain("Releases:           r25.1, r24.2");
        result.Output.ShouldContain("Activity:           Checking r24.2");
        result.Output.ShouldContain("Fetching:           r25.1, downloading p4d (42%)");
        result.Output.ShouldContain("Fetching:           r25.2, queued");
        result.Error.ShouldContain("Last check failed: r24.2: not found");
    }

    private string WriteFile(string name)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, name);
        return path;
    }

    private static BuildDto Build(
        string build,
        long? archiveSize,
        BuildSource source = BuildSource.Ftp,
        string[]? instances = null)
    {
        return new BuildDto(
            Guid.NewGuid(),
            "r25.1",
            "2025.1",
            build,
            source,
            new string('a', 64),
            DateTimeOffset.UnixEpoch,
            archiveSize,
            instances is not null,
            false,
            (instances ?? []).Select(name => new BuildInstanceDto(Guid.NewGuid(), name)).ToList());
    }
}
