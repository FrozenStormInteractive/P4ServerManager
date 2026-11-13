using System.Collections.Concurrent;
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

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Tests.Api;

/// <summary>
/// Restores of backup points made by the daemon to a local destination. A fake p4d dumps the tables, writes the
/// checkpoints and rebuilds a database from them; no p4d can start, so a restore over an instance always ends by putting
/// the previous data back, which these tests check.
/// </summary>
public sealed class RestoresApiTests : IAsyncLifetime
{
    private const string Passphrase = "correct horse battery staple";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly IP4ServerBuildCache _buildCache = Substitute.For<IP4ServerBuildCache>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly ConcurrentQueue<string> _replayed = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private int _journalCounter;
    private ProcessResult? _validation;
    private Action? _onValidation;

    private string DataDirectory => Path.Combine(_directory, "data");

    private string ServerDirectory => Path.Combine(_directory, "server");

    private string RootPath => Path.Combine(ServerDirectory, "root");

    private string JournalsPath => Path.Combine(ServerDirectory, "journals");

    private string DepotPath => Path.Combine(ServerDirectory, "archives", "depot");

    private string BackupsPath => Path.Combine(_directory, "backups");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _buildCache.GetP4dPath(Arg.Any<P4ServerBuild>()).Returns(Path.Combine(_directory, "no-p4d"));
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => RunFakeP4d(call.Arg<ProcessCommand>()));

        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "server.id"), "main\n");
        File.WriteAllText(Path.Combine(RootPath, "db.counters"), "live database");
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
            foreach (string file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Points_AreReadFromTheDestination_EvenWithoutTheHistoryOfTheDaemon()
    {
        Guid instanceId = await CreateInstanceAsync();
        BackupDestinationDto destination = await CreateDestinationAsync();
        await SetDestinationsAsync(instanceId, destination.Id);
        await BackupAndWaitAsync(instanceId, BackupKind.Full);
        await BackupAndWaitAsync(instanceId, BackupKind.Incremental);
        await using (DaemonDbContext db = await CreateDbContextAsync())
        {
            await db.BackupRuns.ExecuteDeleteAsync(Token);
        }

        List<BackupPointDto> points = (await _client.GetFromJsonAsync<List<BackupPointDto>>(
            $"/api/backup-destinations/{destination.Id}/points",
            JsonOptions,
            Token))!;

        points.Select(p => p.Kind).ShouldBe([BackupKind.Incremental, BackupKind.Full]);
        points.ShouldAllBe(p => p.SourceId == instanceId && p.SourceName == "main" && p.CheckpointNumber == 1);
        points[0].JournalNumber.ShouldBe(1);
        points[0].P4dVersion.ShouldBe("2026.1/3089746");
        points[0].CaseSensitive.ShouldBeTrue();
        points[0].ServerId.ShouldBe("main");
        (await _client.GetFromJsonAsync<List<BackupPointDto>>(
            $"/api/backup-destinations/{destination.Id}/points?sourceId={Guid.NewGuid()}",
            JsonOptions,
            Token))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Restore_RebuildsTheDataOfThePoint_ReusesTheUnchangedFiles_ThenPutsThePreviousDataBack_WhenTheServerCannotStart()
    {
        Guid instanceId = await CreateInstanceAsync();
        BackupDestinationDto destination = await CreateDestinationAsync();
        await SetDestinationsAsync(instanceId, destination.Id);
        BackupRunDto full = await BackupAndWaitAsync(instanceId, BackupKind.Full);
        File.WriteAllText(Path.Combine(DepotPath, "new.txt,v"), "head 1.2;\n");

        bool restoredAsThePoint = false;
        _onValidation = () => restoredAsThePoint =
            File.ReadAllText(Path.Combine(RootPath, "db.counters")) == "replayed"
            && File.Exists(Path.Combine(DepotPath, "readme.txt,v"))
            && File.Exists(Path.Combine(DepotPath, "art.png,d", "1.1.gz"))
            && !File.Exists(Path.Combine(DepotPath, "new.txt,v"))
            && File.ReadAllText(Path.Combine(RootPath, "server.id")) == "main\n"
            && File.Exists(Path.Combine(JournalsPath, "main.ckp.1.gz"));

        RestoreRunDto run = await RestoreAndWaitAsync(instanceId, destination.Id, full.Destinations[0].SnapshotKey!);

        restoredAsThePoint.ShouldBeTrue();
        _replayed.ShouldBe(["main.ckp.1.gz"]);
        run.Status.ShouldBe(RestoreRunStatus.Failed);
        run.Error.ShouldNotBeNull();
        run.ReusedFiles.ShouldBe(3);
        run.DownloadedBytes.ShouldBe(2000);
        run.FileCount.ShouldBe(4);
        run.SourceName.ShouldBe("main");
        AssertPreviousDataIsBack();
        (await GetInstanceAsync(instanceId)).LastError.ShouldNotBeNull().ShouldStartWith("The restore failed");
    }

    [Fact]
    public async Task Restore_PutsThePreviousDataBack_WhenTheRestoredDatabaseIsDamaged()
    {
        Guid instanceId = await CreateInstanceAsync();
        BackupDestinationDto destination = await CreateDestinationAsync();
        await SetDestinationsAsync(instanceId, destination.Id);
        await BackupAndWaitAsync(instanceId, BackupKind.Full);
        File.WriteAllText(Path.Combine(DepotPath, "new.txt,v"), "head 1.2;\n");
        BackupRunDto incremental = await BackupAndWaitAsync(instanceId, BackupKind.Incremental);
        _validation = new ProcessResult(
            1,
            "Validating db.rev\n    **** Checksum mismatch **** possible data corruption\n",
            string.Empty);

        RestoreRunDto run = await RestoreAndWaitAsync(instanceId, destination.Id, incremental.Destinations[0].SnapshotKey!);

        _replayed.ShouldBe(["main.ckp.1.gz", "main.jnl.1.gz"]);
        run.Status.ShouldBe(RestoreRunStatus.Failed);
        run.Error.ShouldNotBeNull().ShouldContain("Checksum mismatch");
        AssertPreviousDataIsBack();
    }

    [Fact]
    public async Task Restore_IsRefused_ForAnUnknownDestination_OrAKeyThatIsNoPoint()
    {
        Guid instanceId = await CreateInstanceAsync();

        HttpResponseMessage unknown = await _client.PostAsJsonAsync(
            $"/api/instances/{instanceId}/restores",
            new RestoreBackupRequest { DestinationId = Guid.NewGuid(), SnapshotKey = $"snapshots/{Guid.NewGuid():N}/20261113T100000000Z-{Guid.NewGuid():N}" },
            JsonOptions,
            Token);
        HttpResponseMessage invalid = await _client.PostAsJsonAsync(
            $"/api/instances/{instanceId}/restores",
            new RestoreBackupRequest { DestinationId = Guid.NewGuid(), SnapshotKey = "objects/aa/bb/cc" },
            JsonOptions,
            Token);

        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RestoreInstance_ReadsThePointFirst_AndCreatesTheInstanceWithItsBuildAndCaseHandling()
    {
        Guid instanceId = await CreateInstanceAsync();
        BackupDestinationDto destination = await CreateDestinationAsync();
        await SetDestinationsAsync(instanceId, destination.Id);
        BackupRunDto full = await BackupAndWaitAsync(instanceId, BackupKind.Full);
        string key = full.Destinations[0].SnapshotKey!;
        await AddBuildAsync("r25.1", "2025.1", "2800000");

        HttpResponseMessage older = await PostRestoreInstanceAsync("copy", key, destination.Id, release: "r25.1");
        HttpResponseMessage missing = await PostRestoreInstanceAsync(
            "copy",
            $"snapshots/{instanceId:N}/20261113T100000000Z-{Guid.NewGuid():N}",
            destination.Id);
        HttpResponseMessage created = await PostRestoreInstanceAsync("copy", key, destination.Id);

        older.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await older.Content.ReadAsStringAsync(Token));
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await missing.Content.ReadAsStringAsync(Token));
        created.StatusCode.ShouldBe(HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync(Token));
        InstanceDto instance = (await created.Content.ReadFromJsonAsync<InstanceDto>(JsonOptions, Token))!;
        instance.Name.ShouldBe("copy");
        instance.State.ShouldBe(P4ServerInstanceState.Provisioning);
        instance.Release.ShouldBe("r26.1");
        instance.CaseSensitive.ShouldBeTrue();

        List<RestoreRunDto> restores = (await _client.GetFromJsonAsync<List<RestoreRunDto>>(
            $"/api/instances/{instance.Id}/restores",
            JsonOptions,
            Token))!;
        RestoreRunDto restore = restores.ShouldHaveSingleItem();
        restore.Target.ShouldBe(RestoreTarget.NewInstance);
        restore.SnapshotKey.ShouldBe(key);
        restore.PointId.ShouldBe(full.Id);
        restore.SourceName.ShouldBe("main");
    }

    private void AssertPreviousDataIsBack()
    {
        File.ReadAllText(Path.Combine(RootPath, "db.counters")).ShouldBe("live database");
        File.Exists(Path.Combine(DepotPath, "new.txt,v")).ShouldBeTrue();
        File.Exists(Path.Combine(DepotPath, "readme.txt,v")).ShouldBeTrue();
        File.Exists(Path.Combine(DepotPath, "art.png,d", "1.1.gz")).ShouldBeTrue();
        Directory.GetDirectories(ServerDirectory).Select(Path.GetFileName).Order()
            .ShouldBe(["archives", "journals", "root"]);
        File.Exists(Path.Combine(DataDirectory, "instances")).ShouldBeFalse();
        Directory.EnumerateFiles(DataDirectory, "restore-state.json", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    private ProcessResult RunFakeP4d(ProcessCommand command)
    {
        IReadOnlyList<string> arguments = command.Arguments;
        if (arguments.Contains("-cshow"))
        {
            return Output(
                "any: journalPrefix = ../journals/main\n"
                + "any: server.depot.root = ../archives\n");
        }
        if (arguments.Contains("db.depot"))
        {
            return Output(
                "Dumping to -...\n"
                + "@pv@ 2 @db.depot@ @depot@ 0 @@ @depot/...@ @@ \n"
                + "@pv@ 2 @db.depot@ @spec@ 2 @.p4s@ @spec/...@ @@ \n");
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
        if (arguments.Contains("-jr") || arguments.Contains("-xu"))
        {
            // The replay must not write to the journal of the server: its folder may be set aside.
            arguments.ShouldContain("-J");
            arguments[arguments.ToList().IndexOf("-J") + 1].ShouldNotContain("journals");
        }
        if (arguments.Contains("-jr"))
        {
            _replayed.Enqueue(Path.GetFileName(arguments[^1]));
            File.WriteAllText(Path.Combine(RootPath, "db.counters"), "replayed");
            return Output(string.Empty);
        }
        if (arguments.Contains("-xv"))
        {
            _onValidation?.Invoke();
            return _validation ?? Output("Validating db.counters\n");
        }
        return Output(string.Empty);
    }

    private static ProcessResult Output(string output)
    {
        return new ProcessResult(0, output, string.Empty);
    }

    private async Task<BackupDestinationDto> CreateDestinationAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/backup-destinations",
            new CreateBackupDestinationRequest
            {
                Name = "local",
                Kind = BackupDestinationKind.Local,
                Local = new LocalDestinationSettings { Path = BackupsPath },
                Passphrase = Passphrase,
            },
            JsonOptions,
            Token);
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
        BackupRunDto ended = (await _client.GetFromJsonAsync<BackupRunDto>(
            $"/api/instances/{instanceId}/backups/{run.Id}",
            JsonOptions,
            Token))!;
        ended.Status.ShouldBe(BackupRunStatus.Succeeded, ended.Error);
        return ended;
    }

    private async Task<RestoreRunDto> RestoreAndWaitAsync(Guid instanceId, Guid destinationId, string key)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/instances/{instanceId}/restores",
            new RestoreBackupRequest { DestinationId = destinationId, SnapshotKey = key },
            JsonOptions,
            Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        RestoreRunDto run = (await response.Content.ReadFromJsonAsync<RestoreRunDto>(JsonOptions, Token))!;
        run.Target.ShouldBe(RestoreTarget.ExistingInstance);
        await WaitUntilFreeAsync(instanceId);
        return (await _client.GetFromJsonAsync<RestoreRunDto>(
            $"/api/instances/{instanceId}/restores/{run.Id}",
            JsonOptions,
            Token))!;
    }

    private Task<HttpResponseMessage> PostRestoreInstanceAsync(
        string name,
        string key,
        Guid destinationId,
        string? release = null)
    {
        return _client.PostAsJsonAsync(
            "/api/instances/restore",
            new RestoreInstanceRequest
            {
                Name = name,
                Port = 16672,
                Release = release,
                ServerDirectory = Path.Combine(_directory, "copy"),
                SuperUser = new SuperUserRequest { Name = "admin", Password = "weak" },
                Source = new BackupPointReference { DestinationId = destinationId, SnapshotKey = key },
            },
            JsonOptions,
            Token);
    }

    private async Task<InstanceDto> GetInstanceAsync(Guid instanceId)
    {
        return (await _client.GetFromJsonAsync<InstanceDto>($"/api/instances/{instanceId}", JsonOptions, Token))!;
    }

    /// <summary>
    /// Waits until the operation released the instance.
    /// </summary>
    private async Task WaitUntilFreeAsync(Guid instanceId)
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            InstanceDto instance = await GetInstanceAsync(instanceId);
            if (!instance.RuntimeStatus.IsTransitional())
            {
                // The status is set before the instance is saved and released.
                await Task.Delay(100, Token);
                return;
            }
            await Task.Delay(50, Token);
        }
        throw new TimeoutException("The operation did not end.");
    }

    private async Task<DaemonDbContext> CreateDbContextAsync()
    {
        // The factory creates the database when it starts.
        _ = _factory.Server;
        return await _factory.Services
            .GetRequiredService<IDbContextFactory<DaemonDbContext>>()
            .CreateDbContextAsync(Token);
    }

    private async Task AddBuildAsync(string release, string version, string buildNumber)
    {
        await using DaemonDbContext db = await CreateDbContextAsync();
        db.Builds.Add(new P4ServerBuild
        {
            Id = Guid.NewGuid(),
            Release = release,
            Version = version,
            BuildNumber = buildNumber,
            Platform = P4ServerPlatformDefinition.LinuxX64.Name,
            Sha256 = "b",
            DownloadedAt = DateTimeOffset.UtcNow,
            IsExtracted = true,
        });
        await db.SaveChangesAsync(Token);
    }

    private async Task<Guid> CreateInstanceAsync()
    {
        await using DaemonDbContext db = await CreateDbContextAsync();
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
            Port = 16671,
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
