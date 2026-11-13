using System.Net;
using System.Text.Json;

using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Tests.Support;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Commands;

public sealed class BackupCommandsTests
{
    private readonly CliRunner _cli = new();
    private readonly InstanceDto _main = CliRunner.Instance("main");
    private readonly BackupDestinationDto _nas = Destination("nas", Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private readonly BackupDestinationDto _cloud = Destination("cloud", Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private readonly Guid _runId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    public BackupCommandsTests()
    {
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/instances",
            StubDaemon.Json<IReadOnlyList<InstanceDto>>(HttpStatusCode.OK, [_main], CliJsonContext.Default.IReadOnlyListInstanceDto));
        _cli.Daemon.On(
            HttpMethod.Get,
            "/api/backup-destinations",
            StubDaemon.Json<IReadOnlyList<BackupDestinationDto>>(
                HttpStatusCode.OK,
                [_cloud, _nas],
                CliJsonContext.Default.IReadOnlyListBackupDestinationDto));
    }

    private string BackupsPath => $"/api/instances/{_main.Id}/backups";

    [Fact]
    public async Task Add_CreatesALocalDestination_WithThePassphraseFromTheEnvironment()
    {
        _cli.EnvironmentVariables["P4SM_BACKUP_PASSPHRASE"] = "correct horse battery staple";
        _cli.Daemon.On(
            HttpMethod.Post,
            "/api/backup-destinations",
            StubDaemon.Json(HttpStatusCode.Created, _nas, CliJsonContext.Default.BackupDestinationDto));

        CliResult result = await _cli.RunAsync(
            "backup-destination", "add", "nas", "--kind", "local", "--path", "/volume1/backups");

        result.ExitCode.ShouldBe(0, result.Error);
        result.Output.ShouldContain("Added backup destination nas");
        CreateBackupDestinationRequest request = JsonSerializer.Deserialize(
            _cli.Daemon.RequestsTo(HttpMethod.Post, "/api/backup-destinations").Single().Body!,
            CliJsonContext.Default.CreateBackupDestinationRequest)!;
        request.Kind.ShouldBe(BackupDestinationKind.Local);
        request.Local!.Path.ShouldBe(Path.GetFullPath("/volume1/backups"));
        request.Passphrase.ShouldBe("correct horse battery staple");
        request.S3.ShouldBeNull();
    }

    [Fact]
    public async Task Add_ReadsThePassphraseAndTheSecretFromTheStandardInput()
    {
        _cli.Input = "correct horse battery staple\nsecret-key\n";
        _cli.Daemon.On(
            HttpMethod.Post,
            "/api/backup-destinations",
            StubDaemon.Json(HttpStatusCode.Created, _cloud, CliJsonContext.Default.BackupDestinationDto));

        CliResult result = await _cli.RunAsync(
            "backup-destination", "add", "cloud", "--kind", "s3-compatible", "--bucket", "p4", "--endpoint",
            "https://s3.example.test", "--access-key-id", "AKIA", "--secrets-stdin");

        result.ExitCode.ShouldBe(0, result.Error);
        CreateBackupDestinationRequest request = JsonSerializer.Deserialize(
            _cli.Daemon.RequestsTo(HttpMethod.Post, "/api/backup-destinations").Single().Body!,
            CliJsonContext.Default.CreateBackupDestinationRequest)!;
        request.S3!.Endpoint.ShouldBe("https://s3.example.test");
        request.S3.ForcePathStyle.ShouldBeTrue();
        request.Credentials!.SecretAccessKey.ShouldBe("secret-key");
    }

    [Fact]
    public async Task Add_NeedsTheSecretsWithoutATerminal()
    {
        CliResult result = await _cli.RunAsync("backup-destination", "add", "nas", "--kind", "local", "--path", "/b");

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("P4SM_BACKUP_PASSPHRASE");
        _cli.Daemon.Requests.ShouldNotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Destinations_SetsTheDestinationsOfAnInstanceByName()
    {
        _cli.Daemon.On(
            HttpMethod.Put,
            $"/api/instances/{_main.Id}/backup-destinations",
            StubDaemon.Json(
                HttpStatusCode.OK,
                new InstanceBackupDestinations { DestinationIds = [_nas.Id] },
                CliJsonContext.Default.InstanceBackupDestinations));

        CliResult result = await _cli.RunAsync("instance", "backup", "destinations", "main", "--set", "nas");

        result.ExitCode.ShouldBe(0, result.Error);
        result.Output.ShouldContain("nas");
        _cli.Daemon.RequestsTo(HttpMethod.Put, $"/api/instances/{_main.Id}/backup-destinations").Single().Body!
            .ShouldContain(_nas.Id.ToString());
    }

    [Fact]
    public async Task Start_BacksUpIncrementallyToTheDestinationAskedFor_AndWaitsForTheEnd()
    {
        _cli.Daemon.On(HttpMethod.Post, BackupsPath, Reply(Run(BackupRunStatus.Running), HttpStatusCode.Accepted));
        _cli.Daemon.On(HttpMethod.Get, $"/api/instances/{_main.Id}/operation", StubDaemon.Status(HttpStatusCode.NotFound));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _main, CliJsonContext.Default.InstanceDto));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"{BackupsPath}/{_runId}",
            Reply(Run(BackupRunStatus.Running)),
            Reply(Run(BackupRunStatus.Succeeded)));

        CliResult result = await _cli.RunAsync("instance", "backup", "start", "main", "--incremental", "-d", "cloud");

        result.ExitCode.ShouldBe(0, result.Error);
        result.Output.ShouldContain("Status:");
        result.Output.ShouldContain("Succeeded");
        result.Output.ShouldContain("Warning: Depot cold keeps its archives in an object store");
        StartBackupRequest request = JsonSerializer.Deserialize(
            _cli.Daemon.RequestsTo(HttpMethod.Post, BackupsPath).Single().Body!,
            CliJsonContext.Default.StartBackupRequest)!;
        request.Kind.ShouldBe(BackupKind.Incremental);
        request.DestinationIds.ShouldBe([_cloud.Id]);
    }

    [Fact]
    public async Task Start_ExitsWithAnErrorWhenADestinationFailed()
    {
        _cli.Daemon.On(HttpMethod.Post, BackupsPath, Reply(Run(BackupRunStatus.Running), HttpStatusCode.Accepted));
        _cli.Daemon.On(HttpMethod.Get, $"/api/instances/{_main.Id}/operation", StubDaemon.Status(HttpStatusCode.NotFound));
        _cli.Daemon.On(
            HttpMethod.Get,
            $"/api/instances/{_main.Id}",
            StubDaemon.Json(HttpStatusCode.OK, _main, CliJsonContext.Default.InstanceDto));
        _cli.Daemon.On(HttpMethod.Get, $"{BackupsPath}/{_runId}", Reply(Run(BackupRunStatus.PartiallySucceeded)));

        CliResult result = await _cli.RunAsync("instance", "backup", "start", "main");

        result.ExitCode.ShouldBe(1);
    }

    private Func<HttpResponseMessage> Reply(BackupRunDto run, HttpStatusCode status = HttpStatusCode.OK)
    {
        return StubDaemon.Json(status, run, CliJsonContext.Default.BackupRunDto);
    }

    private BackupRunDto Run(BackupRunStatus status)
    {
        return new BackupRunDto(
            _runId,
            _main.Id,
            _main.Name,
            BackupKind.Incremental,
            BackupTrigger.Manual,
            status,
            new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero),
            status == BackupRunStatus.Running ? null : new DateTimeOffset(2026, 11, 10, 8, 5, 0, TimeSpan.Zero),
            12,
            13,
            4,
            2048,
            null,
            ["Depot cold keeps its archives in an object store: they are not in this backup."],
            [
                new BackupRunDestinationDto(
                    _cloud.Id,
                    "cloud",
                    status == BackupRunStatus.Running ? BackupDestinationStatus.Uploading : BackupDestinationStatus.Succeeded,
                    2,
                    1024,
                    null,
                    null,
                    null),
            ]);
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
