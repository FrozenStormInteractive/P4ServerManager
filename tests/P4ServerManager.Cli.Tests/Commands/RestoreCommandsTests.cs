using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class RestoreCommandsTests
{
    private readonly CliRunner _cli = new();
    private readonly InstanceDto _main = CliRunner.Instance("main");
    private readonly BackupDestinationDto _nas = Destination("nas", Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private readonly Guid _restoreId = Guid.Parse("9b2f0e5a-0000-4000-8000-000000000001");
    private readonly BackupPointDto _older;
    private readonly BackupPointDto _latest;
    private readonly BackupPointDto _other;

    public RestoreCommandsTests()
    {
        _older = Point(Guid.Parse("aaaa1111-0000-4000-8000-000000000000"), _main.Id, "main", BackupKind.Full, 1);
        _latest = Point(Guid.Parse("bbbb2222-0000-4000-8000-000000000000"), _main.Id, "main", BackupKind.Incremental, 2);
        _other = Point(Guid.Parse("cccc3333-0000-4000-8000-000000000000"), Guid.NewGuid(), "legacy", BackupKind.Full, 3);
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(HttpStatusCode.OK, [_main], CliJsonContext.Default.IReadOnlyListInstanceDto));
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/backup-destinations",
            StubDaemon.Json<IReadOnlyList<BackupDestinationDto>>(
                HttpStatusCode.OK,
                [_nas],
                CliJsonContext.Default.IReadOnlyListBackupDestinationDto));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}/backup-destinations",
            StubDaemon.Json(
                HttpStatusCode.OK,
                new InstanceBackupDestinations { DestinationIds = [_nas.Id] },
                CliJsonContext.Default.InstanceBackupDestinations));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/backup-destinations/{_nas.Id}/points",
            Points(_other, _latest, _older));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/backup-destinations/{_nas.Id}/points?sourceId={_main.Id}",
            Points(_latest, _older));
        _cli.Daemon.On(HttpMethod.Get, $"/api/instances/{_main.Id}/operation", StubDaemon.Status(HttpStatusCode.NotFound));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _main, CliJsonContext.Default.InstanceDto));
    }

    private string RestoresPath => $"/api/instances/{_main.Id}/restores";

    [Fact]
    public async Task Points_ListsThePointsOfADestination_ForOneInstance()
    {
        CliResult result = await _cli.RunAsync("backup-destination", "points", "nas", "--instance", "legacy");

        result.ExitCode.ShouldBe(0, result.Error);
        result.Output.ShouldContain("cccc3333");
        result.Output.ShouldNotContain("bbbb2222");
    }

    [Fact]
    public async Task Start_RestoresTheLatestPointOfTheInstance_OnceConfirmed_AndWaitsForTheEnd()
    {
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.ConfirmAnswer = true;
        _cli.Daemon.On(HttpMethod.Post, RestoresPath, Reply(Run(RestoreRunStatus.Running), HttpStatusCode.Accepted));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"{RestoresPath}/{_restoreId}",
            Reply(Run(RestoreRunStatus.Running)),
            Reply(Run(RestoreRunStatus.Succeeded)));

        CliResult result = await _cli.RunAsync("instance", "restore", "start", "main");

        result.ExitCode.ShouldBe(0, result.Error);
        _cli.Prompter.Questions.ShouldHaveSingleItem().ShouldContain("Replace the data of main");
        result.Output.ShouldContain("Succeeded");
        RestoreBackupRequest request = JsonSerializer.Deserialize(
            _cli.Daemon.RequestsTo(HttpMethod.Post, RestoresPath).Single().Body!,
            CliJsonContext.Default.RestoreBackupRequest)!;
        request.DestinationId.ShouldBe(_nas.Id);
        request.SnapshotKey.ShouldBe(_latest.Key);
    }

    [Fact]
    public async Task Start_RestoresThePointOfAnotherInstance_ByItsIdentifier()
    {
        _cli.Daemon.On(HttpMethod.Post, RestoresPath, Reply(Run(RestoreRunStatus.Running), HttpStatusCode.Accepted));

        CliResult result = await _cli.RunAsync("instance", "restore", "start", "main", "cccc", "--yes", "--no-wait");

        result.ExitCode.ShouldBe(0, result.Error);
        _cli.Daemon.RequestsTo(HttpMethod.Post, RestoresPath).Single().Body!.ShouldContain(_other.Key);
    }

    [Fact]
    public async Task Start_AsksForAConfirmation_AndRestoresNothingWithoutIt()
    {
        CliResult withoutTerminal = await _cli.RunAsync("instance", "restore", "start", "main");
        _cli.Prompter.IsInteractive = true;
        _cli.Prompter.ConfirmAnswer = false;
        CliResult refused = await _cli.RunAsync("instance", "restore", "start", "main");

        withoutTerminal.ExitCode.ShouldBe(1);
        withoutTerminal.Error.ShouldContain("--yes");
        refused.ExitCode.ShouldBe(1);
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Start_ExitsWithAnErrorWhenTheVerificationFoundProblems()
    {
        _cli.Daemon.On(HttpMethod.Post, RestoresPath, Reply(Run(RestoreRunStatus.Running), HttpStatusCode.Accepted));
        _cli.Daemon.On(HttpMethod.Get, $"{RestoresPath}/{_restoreId}", Reply(Run(RestoreRunStatus.SucceededWithProblems)));

        CliResult result = await _cli.RunAsync("instance", "restore", "start", "main", "--yes");

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("[problem] //depot/a.txt#1 - MISSING!");
    }

    [Fact]
    public async Task CreateFromBackup_SendsTheLatestPointOfTheInstanceAskedFor_WithThePasswordFromTheStandardInput()
    {
        _cli.Input = "weak\n";
        InstanceDto copy = CliRunner.Instance("copy");
        _cli.Daemon.On(
            HttpMethod.Post,
            "/api/instances/restore",
            StubDaemon.Json(HttpStatusCode.Accepted, copy, CliJsonContext.Default.InstanceDto));

        CliResult result = await _cli.RunAsync(
            "instance", "create-from-backup", "copy", "-d", "nas", "--from", "main", "--port", "1777",
            "--super-user", "admin", "--password-stdin", "--no-license", "--no-wait");

        result.ExitCode.ShouldBe(0, result.Error);
        RestoreInstanceRequest request = JsonSerializer.Deserialize(
            _cli.Daemon.RequestsTo(HttpMethod.Post, "/api/instances/restore").Single().Body!,
            CliJsonContext.Default.RestoreInstanceRequest)!;
        request.Name.ShouldBe("copy");
        request.Port.ShouldBe(1777);
        request.IncludeLicense.ShouldBeFalse();
        request.SuperUser.Password.ShouldBe("weak");
        request.Source.DestinationId.ShouldBe(_nas.Id);
        request.Source.SnapshotKey.ShouldBe(_latest.Key);
    }

    [Fact]
    public async Task CreateFromBackup_NeedsThePointOrTheInstance()
    {
        CliResult result = await _cli.RunAsync(
            "instance", "create-from-backup", "copy", "-d", "nas", "--port", "1777", "--super-user", "admin");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("--point");
    }

    private Func<HttpResponseMessage> Reply(RestoreRunDto run, HttpStatusCode status = HttpStatusCode.OK)
    {
        return StubDaemon.Json(status, run, CliJsonContext.Default.RestoreRunDto);
    }

    private static Func<HttpResponseMessage> Points(params BackupPointDto[] points)
    {
        return StubDaemon.Json<IReadOnlyList<BackupPointDto>>(
            HttpStatusCode.OK,
            points,
            CliJsonContext.Default.IReadOnlyListBackupPointDto);
    }

    private RestoreRunDto Run(RestoreRunStatus status)
    {
        bool problems = status == RestoreRunStatus.SucceededWithProblems;
        return new RestoreRunDto(
            _restoreId,
            _main.Id,
            _main.Name,
            RestoreTarget.ExistingInstance,
            _nas.Id,
            _nas.Name,
            _latest.Key,
            _latest.Id,
            _latest.CreatedAt,
            "main",
            status,
            new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero),
            status == RestoreRunStatus.Running ? null : new DateTimeOffset(2026, 11, 13, 8, 5, 0, TimeSpan.Zero),
            5,
            4096,
            1024,
            3,
            problems ? 1 : 0,
            problems ? ["//depot/a.txt#1 - MISSING!"] : [],
            [],
            null);
    }

    private static BackupPointDto Point(Guid id, Guid sourceId, string sourceName, BackupKind kind, int day)
    {
        DateTimeOffset createdAt = new(2026, 11, day, 8, 0, 0, TimeSpan.Zero);
        return new BackupPointDto(
            $"snapshots/{sourceId:N}/202611{day:00}T080000000Z-{id:N}",
            id,
            sourceId,
            sourceName,
            kind,
            createdAt,
            1,
            kind == BackupKind.Incremental ? 1 : null,
            "2026.1/3089746",
            false,
            true,
            sourceName);
    }

    private static BackupDestinationDto Destination(string name, Guid id)
    {
        return new BackupDestinationDto(
            id,
            name,
            BackupDestinationKind.Local,
            $"/volume1/{name}",
            new LocalDestinationSettings { Path = $"/volume1/{name}" },
            null,
            null,
            null,
            null,
            false,
            Guid.NewGuid(),
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }
}
