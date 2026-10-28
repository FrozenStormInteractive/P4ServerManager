using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Perforce;

using static P4ServerManager.Daemon.Tests.Services.Perforce.BuildTestEnvironment;

namespace P4ServerManager.Daemon.Tests.Services.Perforce;

public sealed class P4ServerBuildCacheTests : IAsyncLifetime
{
    private BuildTestEnvironment _environment = null!;

    private P4ServerBuildCache Cache => _environment.Cache;

    public async ValueTask InitializeAsync()
    {
        _environment = await BuildTestEnvironment.CreateAsync();
        _environment.Publish("r25.1", "2025.1/2850123");
    }

    public async ValueTask DisposeAsync()
    {
        await _environment.DisposeAsync();
    }

    [Fact]
    public async Task EnsureAsync_DownloadsArchivesAndExtractsTheBuild()
    {
        List<BuildProgress> reports = [];

        P4ServerBuild build = await Cache.EnsureAsync(
            Release("r25.1"),
            new InlineProgress<BuildProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        build.Version.ShouldBe("2025.1");
        build.BuildNumber.ShouldBe("2850123");
        build.Sha256.ShouldBe(Hash(P4dContent("2025.1/2850123")));
        build.P4Sha256.ShouldBe(Hash(P4Content("2025.1/2850123")));
        build.Imported.ShouldBeFalse();
        build.IsExtracted.ShouldBeTrue();
        build.ArchiveSize.ShouldNotBeNull().ShouldBeGreaterThan(0);
        File.Exists(Path.Combine(_environment.DataDirectory, "archive", "linux-x64", "2025.1", "2850123.tar.zst"))
            .ShouldBeTrue();
        string expectedDirectory = Path.Combine(_environment.DataDirectory, "builds", "linux-x64", "2025.1", "2850123");
        Cache.GetP4dPath(build).ShouldBe(Path.Combine(expectedDirectory, "p4d"));
        (await File.ReadAllBytesAsync(Cache.GetP4dPath(build), TestContext.Current.CancellationToken))
            .ShouldBe(P4dContent("2025.1/2850123"));
        File.Exists(Cache.GetP4Path(build)).ShouldBeTrue();
        reports.ShouldContain(r => r.Description == "Downloading p4d of r25.1" && r.Percent == 100);
        reports.ShouldContain(r => r.Description == "Downloading p4 of r25.1" && r.Percent == 100);
        reports.ShouldContain(r => r.Description == "Compressing p4d 2025.1/2850123 into the archive" && r.Percent == 100);
        reports.SkipWhile(r => r.Stage == BuildProgressStage.Downloading)
            .ShouldAllBe(r => r.Stage == BuildProgressStage.Compressing);
    }

    [Fact]
    public async Task EnsureAsync_ReusesTheBuildWhenTheChecksumIsUnchanged()
    {
        P4ServerBuild first = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);
        int downloads = _environment.Ftp.Downloads;

        P4ServerBuild second = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);

        second.Id.ShouldBe(first.Id);
        _environment.Ftp.Downloads.ShouldBe(downloads);
    }

    [Fact]
    public async Task EnsureAsync_ExtractsAnArchivedBuildWithoutDownloadingIt()
    {
        (P4ServerBuild acquired, _) = (await Cache.AcquireAsync(Release("r25.1"), null, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        File.Exists(Cache.GetP4dPath(acquired)).ShouldBeFalse();
        int downloads = _environment.Ftp.Downloads;

        P4ServerBuild build = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);

        build.Id.ShouldBe(acquired.Id);
        build.IsExtracted.ShouldBeTrue();
        (await File.ReadAllBytesAsync(Cache.GetP4dPath(build), TestContext.Current.CancellationToken))
            .ShouldBe(P4dContent("2025.1/2850123"));
        _environment.Ftp.Downloads.ShouldBe(downloads);
    }

    [Fact]
    public async Task EnsureAsync_UsesTheNewestArchivedBuildWhenThePerforceFtpIsUnreachable()
    {
        await Cache.AcquireAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);
        _environment.Publish("r25.1", "2025.1/2860000");
        await Cache.AcquireAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);
        _environment.Ftp.Unreachable = true;

        P4ServerBuild build = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);

        build.BuildNumber.ShouldBe("2860000");
        File.Exists(Cache.GetP4dPath(build)).ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureAsync_UsesTheArchiveForAReleaseTheFtpNoLongerPublishes()
    {
        await Cache.AcquireAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);
        _environment.Ftp.Files.Clear();

        P4ServerBuild build = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);

        build.BuildNumber.ShouldBe("2850123");
    }

    [Fact]
    public async Task EnsureAsync_FailsWhenThePerforceFtpIsUnreachableAndTheArchiveLacksTheRelease()
    {
        _environment.Ftp.Unreachable = true;

        await Should.ThrowAsync<HttpRequestException>(
            () => Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureAsync_RejectsADownloadWithAWrongChecksum()
    {
        _environment.Ftp.Files["r25.1/bin.linux26x86_64/p4d"] = "tampered"u8.ToArray();

        await Should.ThrowAsync<P4ServerBuildException>(
            () => Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken));

        await using DaemonDbContext db = _environment.DbContextFactory.CreateDbContext();
        (await db.Builds.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        Directory.Exists(Path.Combine(_environment.DataDirectory, "builds", "linux-x64")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_environment.DataDirectory, "archive", "linux-x64")).ShouldBeFalse();
    }

    [Fact]
    public async Task EnsureAsync_FailsWhenTheReleaseHasNoBuildForThePlatform()
    {
        await Should.ThrowAsync<P4ServerBuildException>(
            () => Cache.EnsureAsync(Release("r24.1"), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureAsync_ExtractsAnExactBuild()
    {
        (P4ServerBuild old, _) = (await Cache.AcquireAsync(Release("r25.1"), null, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        _environment.Publish("r25.1", "2025.1/2860000");

        P4ServerBuild build = await Cache.EnsureAsync(old.Id, null, TestContext.Current.CancellationToken);

        build.BuildNumber.ShouldBe("2850123");
        build.IsExtracted.ShouldBeTrue();
        File.Exists(Cache.GetP4Path(build)).ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureAsync_RefusesADamagedArchive()
    {
        (P4ServerBuild build, _) = (await Cache.AcquireAsync(Release("r25.1"), null, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        await File.WriteAllBytesAsync(
            _environment.Archive.GetPath(build),
            "not zstd"u8.ToArray(),
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<P4ServerBuildException>(
            () => Cache.EnsureAsync(build.Id, null, TestContext.Current.CancellationToken));

        Directory.Exists(Path.GetDirectoryName(Cache.GetP4dPath(build))).ShouldBeFalse();
    }

    [Fact]
    public async Task AcquireAsync_ArchivesANewBuildOnlyOnce()
    {
        (P4ServerBuild build, bool downloaded) =
            (await Cache.AcquireAsync(Release("r25.1"), null, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (P4ServerBuild again, bool downloadedAgain) =
            (await Cache.AcquireAsync(Release("r25.1"), null, TestContext.Current.CancellationToken)).ShouldNotBeNull();

        downloaded.ShouldBeTrue();
        downloadedAgain.ShouldBeFalse();
        again.Id.ShouldBe(build.Id);
        build.IsExtracted.ShouldBeFalse();
        _environment.Archive.Exists(build).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(_environment.DataDirectory, "builds", ".tmp")).ShouldBeEmpty();
    }

    [Fact]
    public async Task AcquireAsync_ReturnsNullForAReleaseWithoutBuildForThePlatform()
    {
        (await Cache.AcquireAsync(Release("r24.1"), null, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ImportAsync_ArchivesTheBuildReportedByP4d()
    {
        string staging = Stage("2024.2/2700000");

        P4ServerBuild build = await Cache.ImportAsync(staging, TestContext.Current.CancellationToken);

        build.Release.ShouldBe("r24.2");
        build.Version.ShouldBe("2024.2");
        build.BuildNumber.ShouldBe("2700000");
        build.Imported.ShouldBeTrue();
        build.IsExtracted.ShouldBeFalse();
        _environment.Archive.Exists(build).ShouldBeTrue();
    }

    [Fact]
    public async Task ImportAsync_RefusesABuildAlreadyInTheArchive()
    {
        await Cache.AcquireAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<P4ServerBuildConflictException>(
            () => Cache.ImportAsync(Stage("2025.1/2850123"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportAsync_RefusesAFileThatIsNotAP4d()
    {
        string staging = Cache.CreateStagingDirectory();
        await File.WriteAllTextAsync(Path.Combine(staging, "p4d"), "garbage", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staging, "p4"), "garbage", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<P4ServerBuildException>(
            () => Cache.ImportAsync(staging, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheArchiveTheFilesAndTheRow()
    {
        P4ServerBuild build = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);

        await Cache.DeleteAsync(build.Id, TestContext.Current.CancellationToken);

        _environment.Archive.Exists(build).ShouldBeFalse();
        File.Exists(Cache.GetP4dPath(build)).ShouldBeFalse();
        await using DaemonDbContext db = _environment.DbContextFactory.CreateDbContext();
        (await db.Builds.AnyAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        await _environment.BuildsClient.Received().BuildDeleted(build.Id);
    }

    [Fact]
    public async Task DeleteAsync_RefusesABuildInUse()
    {
        P4ServerBuild build = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);
        await PinAsync(build);

        await Should.ThrowAsync<P4ServerBuildConflictException>(
            () => Cache.DeleteAsync(build.Id, TestContext.Current.CancellationToken));

        _environment.Archive.Exists(build).ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveExtractedAsync_DeletesTheFilesOfAnUnusedArchivedBuild()
    {
        P4ServerBuild build = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);

        (await Cache.RemoveExtractedAsync(build.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();

        File.Exists(Cache.GetP4dPath(build)).ShouldBeFalse();
        _environment.Archive.Exists(build).ShouldBeTrue();
        await using DaemonDbContext db = _environment.DbContextFactory.CreateDbContext();
        (await db.Builds.SingleAsync(TestContext.Current.CancellationToken)).IsExtracted.ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveExtractedAsync_KeepsABuildInUse()
    {
        P4ServerBuild build = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);
        await PinAsync(build);

        (await Cache.RemoveExtractedAsync(build.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();

        File.Exists(Cache.GetP4dPath(build)).ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveExtractedAsync_WaitsUntilNoOperationRuns()
    {
        P4ServerBuild build = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);
        Guid instanceId = Guid.NewGuid();
        _environment.OperationTracker.Begin(instanceId, "upgrade", []);

        (await Cache.RemoveExtractedAsync(build.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();

        _environment.OperationTracker.Complete(instanceId, null);
        (await Cache.RemoveExtractedAsync(build.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task ArchiveAsync_ArchivesABuildCachedBeforeTheArchiveExisted()
    {
        P4ServerBuild build = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);
        _environment.Archive.Delete(build);
        await using (DaemonDbContext db = _environment.DbContextFactory.CreateDbContext())
        {
            await db.Builds.ExecuteUpdateAsync(
                s => s.SetProperty(b => b.ArchiveSize, (long?)null).SetProperty(b => b.P4Sha256, (string?)null),
                TestContext.Current.CancellationToken);
        }

        (await Cache.ArchiveAsync(build.Id, null, TestContext.Current.CancellationToken)).ShouldBeTrue();

        _environment.Archive.Exists(build).ShouldBeTrue();
        await using DaemonDbContext check = _environment.DbContextFactory.CreateDbContext();
        P4ServerBuild archived = await check.Builds.SingleAsync(TestContext.Current.CancellationToken);
        archived.ArchiveSize.ShouldNotBeNull();
        archived.P4Sha256.ShouldBe(Hash(P4Content("2025.1/2850123")));
    }

    [Fact]
    public async Task ReconcileAsync_ReadsWhetherEachBuildIsExtractedAndArchived()
    {
        P4ServerBuild build = await Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);
        Directory.Delete(Path.GetDirectoryName(Cache.GetP4dPath(build))!, recursive: true);

        await Cache.ReconcileAsync(TestContext.Current.CancellationToken);

        await using DaemonDbContext db = _environment.DbContextFactory.CreateDbContext();
        P4ServerBuild reconciled = await db.Builds.SingleAsync(TestContext.Current.CancellationToken);
        reconciled.IsExtracted.ShouldBeFalse();
        reconciled.ArchiveSize.ShouldBe(new FileInfo(_environment.Archive.GetPath(build)).Length);
    }

    private string Stage(string versionAndBuild)
    {
        string staging = Cache.CreateStagingDirectory();
        File.WriteAllBytes(Path.Combine(staging, "p4d"), P4dContent(versionAndBuild));
        File.WriteAllBytes(Path.Combine(staging, "p4"), P4Content(versionAndBuild));
        return staging;
    }

    private async Task PinAsync(P4ServerBuild build)
    {
        await using DaemonDbContext db = _environment.DbContextFactory.CreateDbContext();
        db.Instances.Add(new P4ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = "main",
            Port = 1666,
            RootPath = "/srv/main/root",
            Release = build.Release,
            BuildId = build.Id,
            State = P4ServerInstanceState.Ready,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
