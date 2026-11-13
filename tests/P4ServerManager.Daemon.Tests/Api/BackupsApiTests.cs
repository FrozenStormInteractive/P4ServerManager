using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using P4ServerManager.Backup.Manifests;
using P4ServerManager.Backup.Repository;
using P4ServerManager.Backup.Targets;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Tests.Api;

/// <summary>
/// Backups of an instance laid out as the daemon creates them, to local destinations. A fake p4d dumps the tables and
/// writes the checkpoints and the journals as the real one does.
/// </summary>
public sealed class BackupsApiTests : IAsyncLifetime
{
    private const string Passphrase = "correct horse battery staple";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly IP4ServerBuildCache _buildCache = Substitute.For<IP4ServerBuildCache>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private int _journalCounter;
    private string _depotDump = "Dumping to -...\n"
        + "@nx@ 4 1794529632 @62@ 2 0 0 0 0 @db.depot@ @@ @@ @@ @@ \n"
        + "@pv@ 2 @db.depot@ @depot@ 0 @@ @depot/...@ @@ \n"
        + "@pv@ 2 @db.depot@ @spec@ 2 @.p4s@ @spec/...@ @@ \n"
        + "@pv@ 2 @db.depot@ @remote@ 1 @@ @//...@ @@ \n"
        + "@ex@ 63264 1794529632\n";

    private string DataDirectory => Path.Combine(_directory, "data");

    private string ServerDirectory => Path.Combine(_directory, "server");

    private string RootPath => Path.Combine(ServerDirectory, "root");

    private string JournalsPath => Path.Combine(ServerDirectory, "journals");

    private string DepotPath => Path.Combine(ServerDirectory, "archives", "depot");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _buildCache.GetP4dPath(Arg.Any<P4ServerBuild>()).Returns("/builds/p4d");
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => RunFakeP4d(call.Arg<ProcessCommand>()));

        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "server.id"), "main\n");
        Directory.CreateDirectory(DepotPath);
        File.WriteAllText(Path.Combine(DepotPath, "readme.txt,v"), "head 1.1;\n");
        Directory.CreateDirectory(Path.Combine(DepotPath, "art.png,d"));
        File.WriteAllBytes(Path.Combine(DepotPath, "art.png,d", "1.1.gz"), RandomNumberGenerator.GetBytes(5000));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Daemon:DataDirectory", DataDirectory);
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(_buildCache));
                services.Replace(ServiceDescriptor.Singleton(_processRunner));
                services.Replace(ServiceDescriptor.Singleton(P4ServerPlatformDefinition.LinuxX64));
                services.RemoveAll<IHostedService>();
            });
        });
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateDestination_CreatesAnEncryptedRepository_AndNeverReturnsItsSecrets()
    {
        string path = Path.Combine(_directory, "backups-a");

        HttpResponseMessage response = await PostDestinationAsync("nas", path);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        string body = await response.Content.ReadAsStringAsync(Token);
        body.ShouldNotContain(Passphrase);
        BackupDestinationDto destination = (await response.Content.ReadFromJsonAsync<BackupDestinationDto>(JsonOptions, Token))!;
        destination.Kind.ShouldBe(BackupDestinationKind.Local);
        destination.Location.ShouldBe(path);
        destination.Local!.Path.ShouldBe(path);
        destination.HasCredentials.ShouldBeFalse();
        File.Exists(Path.Combine(path, "config")).ShouldBeTrue();

        BackupDestinationTestResult test = await PostAsync<BackupDestinationTestResult>(
            $"/api/backup-destinations/{destination.Id}/test",
            new { });
        test.Succeeded.ShouldBeTrue();
        test.Capabilities.ShouldContain("ConditionalWrite");
    }

    [Fact]
    public async Task CreateDestination_AttachesAnExistingRepository_OnlyWithItsPassphrase()
    {
        string path = Path.Combine(_directory, "backups-a");
        BackupDestinationDto first = await CreateDestinationAsync("nas", path);
        (await _client.DeleteAsync($"/api/backup-destinations/{first.Id}", Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage wrong = await PostDestinationAsync("nas", path, passphrase: "another long passphrase");
        HttpResponseMessage right = await PostDestinationAsync("nas", path);

        wrong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        right.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await right.Content.ReadFromJsonAsync<BackupDestinationDto>(JsonOptions, Token))!.RepositoryId
            .ShouldBe(first.RepositoryId);
    }

    [Fact]
    public async Task CreateDestination_RejectsAShortPassphrase_AndTheSettingsOfAnotherKind()
    {
        HttpResponseMessage shortPassphrase = await PostDestinationAsync("nas", Path.Combine(_directory, "a"), passphrase: "short");
        HttpResponseMessage otherKind = await _client.PostAsJsonAsync(
            "/api/backup-destinations",
            new
            {
                name = "nas",
                kind = "Local",
                local = new { path = Path.Combine(_directory, "a") },
                gcs = new { bucket = "bucket" },
                passphrase = Passphrase,
            },
            Token);

        shortPassphrase.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        otherKind.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteDestination_IsRefusedWhileAnInstanceBacksUpToIt()
    {
        Guid instanceId = await CreateInstanceAsync();
        BackupDestinationDto destination = await CreateDestinationAsync("nas", Path.Combine(_directory, "a"));
        await SetDestinationsAsync(instanceId, destination.Id);

        HttpResponseMessage response = await _client.DeleteAsync($"/api/backup-destinations/{destination.Id}", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Backup_IsRefusedForAnInstanceWithoutDestination()
    {
        Guid instanceId = await CreateInstanceAsync();

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{instanceId}/backups",
            new StartBackupRequest { Kind = BackupKind.Full },
            JsonOptions,
            Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Backup_FullThenIncremental_UploadsToEveryDestinationOnlyWhatItLacks()
    {
        Guid instanceId = await CreateInstanceAsync();
        string pathA = Path.Combine(_directory, "backups-a");
        string pathB = Path.Combine(_directory, "backups-b");
        BackupDestinationDto a = await CreateDestinationAsync("a", pathA);
        BackupDestinationDto b = await CreateDestinationAsync("b", pathB);
        await SetDestinationsAsync(instanceId, a.Id, b.Id);

        BackupRunDto full = await BackupAndWaitAsync(instanceId, BackupKind.Full);

        full.Status.ShouldBe(BackupRunStatus.Succeeded);
        full.CheckpointNumber.ShouldBe(1);
        // The checkpoint, two archives and the server ID.
        full.FileCount.ShouldBe(4);
        full.Destinations.Select(d => d.DestinationName).ShouldBe(["a", "b"]);
        full.Destinations.ShouldAllBe(d => d.Status == BackupDestinationStatus.Succeeded && d.UploadedObjects == 4);
        File.Exists(Path.Combine(JournalsPath, "main.ckp.1.gz")).ShouldBeTrue();

        File.WriteAllText(Path.Combine(DepotPath, "new.txt,v"), "head 1.2;\n");
        BackupRunDto incremental = await BackupAndWaitAsync(instanceId, BackupKind.Incremental);

        incremental.Status.ShouldBe(BackupRunStatus.Succeeded);
        incremental.JournalNumber.ShouldBe(1);
        incremental.CheckpointNumber.ShouldBe(1);
        // The journal and the new archive only.
        incremental.Destinations.ShouldAllBe(d => d.UploadedObjects == 2);

        BackupManifest manifest = await ReadManifestAsync(pathA, incremental.Destinations[0].SnapshotKey!);
        manifest.Header.Kind.ShouldBe("incremental");
        manifest.Header.BaseId.ShouldBe(full.Id);
        manifest.Header.Labels["checkpoint"].ShouldBe("1");
        manifest.Header.Labels["journal"].ShouldBe("1");
        manifest.Entries.Select(e => (e.Role, e.Path)).OrderBy(e => e.Path).ShouldBe(
        [
            ("archive", "archives/depot/art.png,d/1.1.gz"),
            ("archive", "archives/depot/new.txt,v"),
            ("archive", "archives/depot/readme.txt,v"),
            ("checkpoint", "journals/main.ckp.1.gz"),
            ("journal", "journals/main.jnl.1.gz"),
            ("root-file", "root/server.id"),
        ]);
    }

    [Fact]
    public async Task Backup_Incremental_FailsOnlyTheDestinationThatHasNoFullBackup()
    {
        Guid instanceId = await CreateInstanceAsync();
        BackupDestinationDto a = await CreateDestinationAsync("a", Path.Combine(_directory, "backups-a"));
        BackupDestinationDto b = await CreateDestinationAsync("b", Path.Combine(_directory, "backups-b"));
        await SetDestinationsAsync(instanceId, a.Id);
        await BackupAndWaitAsync(instanceId, BackupKind.Full);
        await SetDestinationsAsync(instanceId, a.Id, b.Id);

        BackupRunDto incremental = await BackupAndWaitAsync(instanceId, BackupKind.Incremental);

        incremental.Status.ShouldBe(BackupRunStatus.PartiallySucceeded);
        incremental.Destinations[0].Status.ShouldBe(BackupDestinationStatus.Succeeded);
        incremental.Destinations[1].Status.ShouldBe(BackupDestinationStatus.Failed);
        incremental.Destinations[1].Error!.ShouldContain("full backup");
    }

    [Fact]
    public async Task Backup_Incremental_FailsWhenNoDestinationHasTheJournalsBefore()
    {
        Guid instanceId = await CreateInstanceAsync();
        BackupDestinationDto a = await CreateDestinationAsync("a", Path.Combine(_directory, "backups-a"));
        await SetDestinationsAsync(instanceId, a.Id);
        await BackupAndWaitAsync(instanceId, BackupKind.Full);
        // A rotation outside the backups.
        _journalCounter++;

        BackupRunDto incremental = await BackupAndWaitAsync(instanceId, BackupKind.Incremental);

        incremental.Status.ShouldBe(BackupRunStatus.Failed);
        incremental.Destinations[0].Error!.ShouldContain("misses journals 1 to 1");
        incremental.Destinations[0].UploadedObjects.ShouldBe(0);
    }

    [Fact]
    public async Task Backup_TakesTheDepotRootWhole_EvenTheFoldersOfNoKnownDepot()
    {
        Guid instanceId = await CreateInstanceAsync();
        string path = Path.Combine(_directory, "backups-a");
        BackupDestinationDto a = await CreateDestinationAsync("a", path);
        await SetDestinationsAsync(instanceId, a.Id);
        string other = Path.Combine(ServerDirectory, "archives", "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "file.txt,v"), "head 1.1;\n");

        BackupRunDto full = await BackupAndWaitAsync(instanceId, BackupKind.Full);

        full.Status.ShouldBe(BackupRunStatus.Succeeded);
        BackupManifest manifest = await ReadManifestAsync(path, full.Destinations[0].SnapshotKey!);
        manifest.Entries.ShouldContain(e => e.Path == "archives/other/file.txt,v");
    }

    [Fact]
    public async Task Backup_TakesTheDepotsOutsideTheDepotRoot_UnderTheirName()
    {
        Guid instanceId = await CreateInstanceAsync();
        string path = Path.Combine(_directory, "backups-a");
        BackupDestinationDto a = await CreateDestinationAsync("a", path);
        await SetDestinationsAsync(instanceId, a.Id);
        string elsewhere = Path.Combine(_directory, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "sub"));
        File.WriteAllText(Path.Combine(elsewhere, "sub", "far.txt,v"), "head 1.1;\n");
        _depotDump += $"@pv@ 2 @db.depot@ @far@ 0 @@ @{elsewhere.Replace('\\', '/')}/...@ @@ \n";

        BackupRunDto full = await BackupAndWaitAsync(instanceId, BackupKind.Full);

        full.Status.ShouldBe(BackupRunStatus.Succeeded);
        BackupManifest manifest = await ReadManifestAsync(path, full.Destinations[0].SnapshotKey!);
        manifest.Entries.ShouldContain(e => e.Path == "depots/far/sub/far.txt,v");
        manifest.Entries.ShouldContain(e => e.Path == "archives/depot/readme.txt,v");
    }

    [Fact]
    public async Task Backup_LeavesOutADepotInAnObjectStore_WithAWarning_AndKeepsItsStoreInTheManifest()
    {
        Guid instanceId = await CreateInstanceAsync();
        string path = Path.Combine(_directory, "backups-a");
        BackupDestinationDto a = await CreateDestinationAsync("a", path);
        await SetDestinationsAsync(instanceId, a.Id);
        _depotDump += "@pv@ 2 @db.depot@ @cold@ 0 @@ @cold/...@ @s3,bucket:cold,secret:hidden@ \n";

        BackupRunDto full = await BackupAndWaitAsync(instanceId, BackupKind.Full);

        full.Status.ShouldBe(BackupRunStatus.Succeeded);
        full.Warnings.ShouldHaveSingleItem().ShouldContain("Depot cold keeps its archives in an object store");
        full.Warnings[0].ShouldNotContain("hidden");
        BackupManifest manifest = await ReadManifestAsync(path, full.Destinations[0].SnapshotKey!);
        manifest.Header.Labels["objectStoreDepot:cold"].ShouldBe("s3,bucket:cold,secret:hidden");
    }

    [Theory]
    [InlineData("@pv@ 3 @db.depot@ @depot@ 0 @@ @depot/...@ @@ @new@ \n", "only version 2 is known")]
    [InlineData("@pv@ 2 @db.depot@ @depot@ 0 @@ @depot/...@ \n", "columns instead of 5")]
    public async Task Backup_FailsBeforeTouchingTheServer_WhenTheDepotsAreNotKnownForSure(string depots, string error)
    {
        Guid instanceId = await CreateInstanceAsync();
        BackupDestinationDto a = await CreateDestinationAsync("a", Path.Combine(_directory, "backups-a"));
        await SetDestinationsAsync(instanceId, a.Id);
        _depotDump = "Dumping to -...\n" + depots;

        BackupRunDto full = await BackupAndWaitAsync(instanceId, BackupKind.Full);

        full.Status.ShouldBe(BackupRunStatus.Failed);
        full.Error!.ShouldContain(error);
        Directory.Exists(JournalsPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Verify_FindsAPointComplete_ThenAnObjectChanged()
    {
        Guid instanceId = await CreateInstanceAsync();
        string path = Path.Combine(_directory, "backups-a");
        BackupDestinationDto a = await CreateDestinationAsync("a", path);
        await SetDestinationsAsync(instanceId, a.Id);
        BackupRunDto full = await BackupAndWaitAsync(instanceId, BackupKind.Full);

        BackupVerificationDto verified = await VerifyAndWaitAsync(instanceId, full.Id, a.Id);
        string objectFile = Directory.EnumerateFiles(Path.Combine(path, "objects"), "*", SearchOption.AllDirectories).First();
        byte[] bytes = await File.ReadAllBytesAsync(objectFile, Token);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(objectFile, bytes, Token);
        BackupVerificationDto damaged = await VerifyAndWaitAsync(instanceId, full.Id, a.Id);

        verified.Succeeded.ShouldBeTrue();
        verified.Deep.ShouldBeTrue();
        verified.CheckedObjects.ShouldBe(4);
        damaged.Succeeded.ShouldBeFalse();
        damaged.ProblemCount.ShouldBe(1);
    }

    private ProcessResult RunFakeP4d(ProcessCommand command)
    {
        IReadOnlyList<string> arguments = command.Arguments;
        if (arguments.Contains("-cshow"))
        {
            return Output(
                "any: journalPrefix = ../journals/main\n"
                + "any: server.depot.root = ../archives\n"
                + "other: journalPrefix = elsewhere\n");
        }
        if (arguments.Contains("db.depot"))
        {
            return Output(_depotDump);
        }
        if (arguments.Contains("-jc"))
        {
            Directory.CreateDirectory(JournalsPath);
            File.WriteAllBytes(Path.Combine(JournalsPath, $"main.jnl.{_journalCounter}.gz"), RandomNumberGenerator.GetBytes(100));
            _journalCounter++;
            File.WriteAllBytes(Path.Combine(JournalsPath, $"main.ckp.{_journalCounter}.gz"), RandomNumberGenerator.GetBytes(2000));
            return Output(string.Empty);
        }
        if (arguments.Contains("-jj"))
        {
            Directory.CreateDirectory(JournalsPath);
            File.WriteAllBytes(Path.Combine(JournalsPath, $"main.jnl.{_journalCounter}.gz"), RandomNumberGenerator.GetBytes(300));
            _journalCounter++;
            return Output(string.Empty);
        }
        return Output(string.Empty);
    }

    private static ProcessResult Output(string output)
    {
        return new ProcessResult(0, output, string.Empty);
    }

    private Task<HttpResponseMessage> PostDestinationAsync(string name, string path, string passphrase = Passphrase)
    {
        return _client.PostAsJsonAsync(
            "/api/backup-destinations",
            new CreateBackupDestinationRequest
            {
                Name = name,
                Kind = BackupDestinationKind.Local,
                Local = new LocalDestinationSettings { Path = path },
                Passphrase = passphrase,
            },
            JsonOptions,
            Token);
    }

    private async Task<BackupDestinationDto> CreateDestinationAsync(string name, string path)
    {
        HttpResponseMessage response = await PostDestinationAsync(name, path);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<BackupDestinationDto>(JsonOptions, Token))!;
    }

    private async Task SetDestinationsAsync(Guid instanceId, params Guid[] destinationIds)
    {
        HttpResponseMessage response = await _client.PutAsJsonAsync(
            $"/api/instances/{instanceId}/backup-destinations",
            new InstanceBackupDestinations { DestinationIds = destinationIds },
            JsonOptions,
            Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
    }

    private async Task<T> PostAsync<T>(string uri, object body)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(uri, body, JsonOptions, Token);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, Token))!;
    }

    private async Task<BackupRunDto> BackupAndWaitAsync(Guid instanceId, BackupKind kind)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{instanceId}/backups",
            new StartBackupRequest { Kind = kind },
            JsonOptions,
            Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        BackupRunDto run = (await response.Content.ReadFromJsonAsync<BackupRunDto>(JsonOptions, Token))!;
        await WaitUntilFreeAsync(instanceId);
        return (await _client.GetFromJsonAsync<BackupRunDto>(
            $"/api/instances/{instanceId}/backups/{run.Id}",
            JsonOptions,
            Token))!;
    }

    private async Task<BackupVerificationDto> VerifyAndWaitAsync(Guid instanceId, Guid runId, Guid destinationId)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{instanceId}/backups/{runId}/verify",
            new VerifyBackupRequest { DestinationId = destinationId, Deep = true },
            JsonOptions,
            Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        await WaitUntilFreeAsync(instanceId);
        BackupRunDto run = (await _client.GetFromJsonAsync<BackupRunDto>(
            $"/api/instances/{instanceId}/backups/{runId}",
            JsonOptions,
            Token))!;
        return run.Destinations.Single(d => d.DestinationId == destinationId).LastVerification!;
    }

    /// <summary>
    /// Waits until the operation released the instance.
    /// </summary>
    private async Task WaitUntilFreeAsync(Guid instanceId)
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            InstanceDto instance = (await _client.GetFromJsonAsync<InstanceDto>(
                $"/api/instances/{instanceId}",
                JsonOptions,
                Token))!;
            if (!instance.RuntimeStatus.IsTransitional())
            {
                // The status is set before the instance is saved and released.
                await Task.Delay(50, Token);
                return;
            }
            await Task.Delay(50, Token);
        }
        throw new TimeoutException("The operation did not end.");
    }

    private static async Task<BackupManifest> ReadManifestAsync(string path, string key)
    {
        await using LocalBackupTarget target = new(path);
        using BackupRepository repository = await BackupRepository.OpenAsync(
            target,
            Passphrase,
            new BackupRepositoryOptions(Path.Combine(path, "..", "tmp")),
            Token);
        return await repository.ReadManifestAsync(key, Token);
    }

    private async Task<Guid> CreateInstanceAsync()
    {
        // The factory creates the database when it starts.
        _ = _factory.Server;

        await using DaemonDbContext db = await _factory.Services
            .GetRequiredService<IDbContextFactory<DaemonDbContext>>()
            .CreateDbContextAsync(Token);
        P4ServerBuild build = new()
        {
            Id = Guid.NewGuid(),
            Release = "r26.1",
            Version = "2026.1",
            BuildNumber = "3089746",
            Platform = P4ServerPlatformDefinition.LinuxX64.Name,
            Sha256 = "a",
            DownloadedAt = DateTimeOffset.UtcNow,
            IsExtracted = true,
        };
        P4ServerInstance instance = new()
        {
            Id = Guid.NewGuid(),
            Name = "main",
            Port = 16661,
            RootPath = RootPath,
            Release = "r26.1",
            Build = build,
            CaseSensitive = true,
            State = P4ServerInstanceState.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Instances.Add(instance);
        await db.SaveChangesAsync(Token);
        _factory.Services.GetRequiredService<InstanceRuntimeRegistry>().Set(instance.Id, InstanceRuntimeStatus.Stopped);
        return instance.Id;
    }
}
