using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Builds;
using P4ServerManager.Daemon.Tests.Services.Perforce;

using static P4ServerManager.Daemon.Tests.Services.Perforce.BuildTestEnvironment;

namespace P4ServerManager.Daemon.Tests.Services.Builds;

public sealed class BuildArchiveSynchronizerTests : IAsyncLifetime
{
    private BuildTestEnvironment _environment = null!;

    private BuildArchiveSynchronizer Synchronizer => _environment.Synchronizer;

    public async ValueTask InitializeAsync()
    {
        _environment = await BuildTestEnvironment.CreateAsync();
        _environment.Publish("r25.1", "2025.1/2850123");
        _environment.Publish("r24.2", "2024.2/2700000");
    }

    public async ValueTask DisposeAsync()
    {
        await _environment.DisposeAsync();
    }

    [Fact]
    public async Task CheckAsync_DownloadsTheNewBuildsOfTheSelectedReleases()
    {
        BuildArchivePolicy policy = new() { Scope = BuildArchiveScope.SelectedReleases, Releases = ["r25.1"] };
        List<string> reports = [];

        BuildArchiveCheckResult result = await Synchronizer.CheckAsync(
            policy,
            (release, progress) => reports.Add(progress?.Description ?? $"Checking {release}"),
            TestContext.Current.CancellationToken);

        result.Errors.ShouldBeEmpty();
        result.Downloaded.Select(b => b.BuildNumber).ShouldBe(["2850123"]);
        reports.ShouldContain("Checking r25.1");
        reports.ShouldContain("Compressing p4d 2025.1/2850123 into the archive");
        reports.ShouldNotContain("Checking r24.2");
    }

    [Fact]
    public async Task CheckAsync_DownloadsEveryReleaseFromTheMinimumWithAllReleases()
    {
        _environment.Publish("r23.2", "2023.2/2500000");
        BuildArchivePolicy policy = new() { Scope = BuildArchiveScope.AllReleases };

        BuildArchiveCheckResult result = await Synchronizer.CheckAsync(
            policy,
            (_, _) => { },
            TestContext.Current.CancellationToken);

        result.Downloaded.Select(b => b.Release).Order().ShouldBe(["r24.2", "r25.1"]);
    }

    [Fact]
    public async Task CheckAsync_OnlyDownloadsAPatchOnceItsChecksumChanges()
    {
        BuildArchivePolicy policy = new() { Scope = BuildArchiveScope.SelectedReleases, Releases = ["r25.1"] };
        await Synchronizer.CheckAsync(policy, (_, _) => { }, TestContext.Current.CancellationToken);

        BuildArchiveCheckResult unchanged = await Synchronizer.CheckAsync(
            policy,
            (_, _) => { },
            TestContext.Current.CancellationToken);
        _environment.Publish("r25.1", "2025.1/2860000");
        BuildArchiveCheckResult patched = await Synchronizer.CheckAsync(
            policy,
            (_, _) => { },
            TestContext.Current.CancellationToken);

        unchanged.Downloaded.ShouldBeEmpty();
        patched.Downloaded.Select(b => b.BuildNumber).ShouldBe(["2860000"]);
        await using DaemonDbContext db = _environment.DbContextFactory.CreateDbContext();
        (await db.Builds.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task CheckAsync_ReportsAFailingReleaseAndChecksTheOthers()
    {
        _environment.Ftp.Files["r25.1/bin.linux26x86_64/p4d"] = "tampered"u8.ToArray();
        BuildArchivePolicy policy = new() { Scope = BuildArchiveScope.SelectedReleases, Releases = ["r25.1", "r24.2"] };

        BuildArchiveCheckResult result = await Synchronizer.CheckAsync(
            policy,
            (_, _) => { },
            TestContext.Current.CancellationToken);

        result.Downloaded.Select(b => b.Release).ShouldBe(["r24.2"]);
        result.Errors.ShouldHaveSingleItem().ShouldStartWith("r25.1: ");
    }

    [Fact]
    public async Task CheckAsync_ReportsAnUnreachablePerforceFtp()
    {
        _environment.Ftp.Unreachable = true;

        BuildArchiveCheckResult result = await Synchronizer.CheckAsync(
            new BuildArchivePolicy { Scope = BuildArchiveScope.AllReleases },
            (_, _) => { },
            TestContext.Current.CancellationToken);

        result.Errors.ShouldHaveSingleItem().ShouldStartWith("Perforce could not be reached");
    }

    [Fact]
    public async Task ApplyRetentionAsync_KeepsTheNewestBuildsOfEachRelease()
    {
        await AcquireAsync("r25.1", "2025.1/2800000");
        await AcquireAsync("r25.1", "2025.1/2850123");
        await AcquireAsync("r25.1", "2025.1/2860000");
        await AcquireAsync("r24.2", "2024.2/2700000");

        int removed = await Synchronizer.ApplyRetentionAsync(
            new BuildArchivePolicy { KeepLatestPerRelease = 2 },
            TestContext.Current.CancellationToken);

        removed.ShouldBe(1);
        (await GetBuildNumbersAsync()).ShouldBe(["2700000", "2850123", "2860000"]);
    }

    [Fact]
    public async Task ApplyRetentionAsync_SparesTheBuildsInUseAndTheProtectedOnes()
    {
        P4ServerBuild used = await AcquireAsync("r25.1", "2025.1/2700000");
        P4ServerBuild kept = await AcquireAsync("r25.1", "2025.1/2800000");
        await AcquireAsync("r25.1", "2025.1/2850123");
        await AcquireAsync("r25.1", "2025.1/2860000");
        await using (DaemonDbContext db = _environment.DbContextFactory.CreateDbContext())
        {
            await db.Builds.Where(b => b.Id == kept.Id).ExecuteUpdateAsync(
                s => s.SetProperty(b => b.Protected, true),
                TestContext.Current.CancellationToken);
            db.Instances.Add(new P4ServerInstance
            {
                Id = Guid.NewGuid(),
                Name = "main",
                Port = 1666,
                RootPath = "/srv/main/root",
                Release = "r25.1",
                BuildId = used.Id,
                State = P4ServerInstanceState.Ready,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        int removed = await Synchronizer.ApplyRetentionAsync(
            new BuildArchivePolicy { KeepLatestPerRelease = 1 },
            TestContext.Current.CancellationToken);

        removed.ShouldBe(1);
        (await GetBuildNumbersAsync()).ShouldBe(["2700000", "2800000", "2860000"]);
    }

    [Fact]
    public async Task ApplyRetentionAsync_KeepsEverythingWithoutALimit()
    {
        await AcquireAsync("r25.1", "2025.1/2800000");
        await AcquireAsync("r25.1", "2025.1/2850123");

        int removed = await Synchronizer.ApplyRetentionAsync(
            new BuildArchivePolicy { KeepLatestPerRelease = null },
            TestContext.Current.CancellationToken);

        removed.ShouldBe(0);
    }

    private async Task<P4ServerBuild> AcquireAsync(string release, string versionAndBuild)
    {
        _environment.Publish(release, versionAndBuild);
        (P4ServerBuild build, _) = (await _environment.Cache.AcquireAsync(
            Release(release),
            null,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
        return build;
    }

    private async Task<List<string>> GetBuildNumbersAsync()
    {
        await using DaemonDbContext db = _environment.DbContextFactory.CreateDbContext();
        return await db.Builds
            .Select(b => b.BuildNumber)
            .OrderBy(b => b)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
