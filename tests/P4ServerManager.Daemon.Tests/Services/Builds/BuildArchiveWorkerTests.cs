using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Builds;
using P4ServerManager.Daemon.Tests.Services.Perforce;

using static P4ServerManager.Daemon.Tests.Services.Perforce.BuildTestEnvironment;

namespace P4ServerManager.Daemon.Tests.Services.Builds;

public sealed class BuildArchiveWorkerTests : IAsyncLifetime
{
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private BuildTestEnvironment _environment = null!;
    private BuildArchiveWorker _worker = null!;

    public async ValueTask InitializeAsync()
    {
        _environment = await BuildTestEnvironment.CreateAsync();
        _environment.Publish("r25.1", "2025.1/2850123");
        _worker = new BuildArchiveWorker(
            _environment.Cache,
            _environment.Synchronizer,
            _environment.DbContextFactory,
            _environment.Notifier,
            _time,
            NullLogger<BuildArchiveWorker>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _worker.Dispose();
        await _environment.DisposeAsync();
    }

    [Fact]
    public async Task RunOnceAsync_ChecksThePerforceFtpWhenTheIntervalHasElapsed()
    {
        await SavePolicyAsync(new BuildArchivePolicy
        {
            AutoDownload = true,
            CheckIntervalHours = 6,
            Releases = ["r25.1"],
            LastCheckAt = _time.GetUtcNow().AddHours(-7),
        });

        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);

        (await CountBuildsAsync()).ShouldBe(1);
        BuildArchiveStatusDto status = await _worker.GetStatusAsync(TestContext.Current.CancellationToken);
        status.Running.ShouldBeFalse();
        status.LastCheckAt.ShouldBe(_time.GetUtcNow());
        status.LastCheckError.ShouldBeNull();
        status.NextCheckAt.ShouldBe(_time.GetUtcNow().AddHours(6));
    }

    [Fact]
    public async Task RunOnceAsync_WaitsForTheInterval()
    {
        await SavePolicyAsync(new BuildArchivePolicy
        {
            AutoDownload = true,
            CheckIntervalHours = 6,
            Releases = ["r25.1"],
            LastCheckAt = _time.GetUtcNow().AddHours(-1),
        });

        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);

        (await CountBuildsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task RunOnceAsync_ChecksOnRequestWithTheAutomaticDownloadOff()
    {
        await SavePolicyAsync(new BuildArchivePolicy { AutoDownload = false, Releases = ["r25.1"] });

        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);
        (await CountBuildsAsync()).ShouldBe(0);

        _worker.RequestCheck();
        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);
        (await CountBuildsAsync()).ShouldBe(1);
        (await _worker.GetStatusAsync(TestContext.Current.CancellationToken)).NextCheckAt.ShouldBeNull();
    }

    [Fact]
    public async Task RequestCheck_RefusesASecondCheckUntilTheFirstIsOver()
    {
        await SavePolicyAsync(new BuildArchivePolicy { AutoDownload = false, Releases = ["r25.1"] });

        _worker.RequestCheck().ShouldBeTrue();
        _worker.RequestCheck().ShouldBeFalse();

        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);

        _worker.RequestCheck().ShouldBeTrue();
    }

    [Fact]
    public async Task GetStatusAsync_TellsTheWorkerIsRunningForTheWholePass()
    {
        await SavePolicyAsync(new BuildArchivePolicy { AutoDownload = false, Releases = ["r25.1"] });
        List<bool> running = [];
        _environment.Ftp.OnRequest = () => running.Add(
            _worker.GetStatusAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult().Running);

        _worker.RequestCheck();
        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);

        running.ShouldNotBeEmpty();
        running.ShouldAllBe(r => r);
        (await _worker.GetStatusAsync(TestContext.Current.CancellationToken)).Running.ShouldBeFalse();
    }

    [Fact]
    public async Task RunOnceAsync_DownloadsARequestedRelease()
    {
        _worker.RequestDownload(Release("r25.1"));

        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);

        (await CountBuildsAsync()).ShouldBe(1);
        (await _worker.GetStatusAsync(TestContext.Current.CancellationToken)).LastDownloadError.ShouldBeNull();
    }

    [Fact]
    public async Task RequestDownload_QueuesEachReleaseOnce()
    {
        _worker.RequestDownload(Release("r25.1")).ShouldBeTrue();
        _worker.RequestDownload(Release("r24.2")).ShouldBeTrue();
        _worker.RequestDownload(Release("r25.1")).ShouldBeFalse();

        BuildArchiveStatusDto status = await _worker.GetStatusAsync(TestContext.Current.CancellationToken);

        status.Fetches.Select(f => (f.Release, f.State)).ShouldBe(
            [("r25.1", BuildFetchState.Queued), ("r24.2", BuildFetchState.Queued)]);
        status.Running.ShouldBeTrue();
    }

    [Fact]
    public async Task RunOnceAsync_ShowsTheFetchUnderWayBeforeTheQueuedOnes()
    {
        _environment.Publish("r24.2", "2024.2/2700000");
        _worker.RequestDownload(Release("r25.1"));
        _worker.RequestDownload(Release("r24.2"));
        List<BuildFetchDto[]> seen = [];
        _environment.Ftp.OnRequest = () => seen.Add(
            [.. _worker.GetStatusAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult().Fetches]);

        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);

        seen.ShouldContain(fetches => fetches.Length == 2
            && fetches[0].Release == "r25.1" && fetches[0].State == BuildFetchState.Downloading
            && fetches[1].Release == "r24.2" && fetches[1].State == BuildFetchState.Queued);
        seen.ShouldContain(fetches => fetches.Length == 1
            && fetches[0].Release == "r24.2" && fetches[0].State != BuildFetchState.Queued);
        (await _worker.GetStatusAsync(TestContext.Current.CancellationToken)).Fetches.ShouldBeEmpty();
        (await CountBuildsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task RunOnceAsync_ListsTheNewBuildsOfACheckWhileItFetchesThem()
    {
        await SavePolicyAsync(new BuildArchivePolicy { AutoDownload = false, Releases = ["r25.1"] });
        List<BuildArchiveStatusDto> seen = [];
        _environment.Ftp.OnRequest = () => seen.Add(
            _worker.GetStatusAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        _worker.RequestCheck();
        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);

        seen.ShouldAllBe(status => status.Checking);
        seen.ShouldContain(status => status.Fetches.Any(f => f.Release == "r25.1"
            && f.State == BuildFetchState.Downloading));
        BuildArchiveStatusDto after = await _worker.GetStatusAsync(TestContext.Current.CancellationToken);
        after.Checking.ShouldBeFalse();
        after.Fetches.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunOnceAsync_TellsWhyARequestedDownloadFailed()
    {
        _worker.RequestDownload(Release("r24.1"));

        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);

        (await _worker.GetStatusAsync(TestContext.Current.CancellationToken)).LastDownloadError
            .ShouldBe("r24.1 has no p4d build for this platform.");
    }

    [Fact]
    public async Task RunOnceAsync_RemovesTheExtractedFilesOfUnusedBuilds()
    {
        await _environment.Cache.EnsureAsync(Release("r25.1"), null, TestContext.Current.CancellationToken);

        await _worker.RunOnceAsync(TestContext.Current.CancellationToken);

        await using DaemonDbContext db = _environment.DbContextFactory.CreateDbContext();
        P4ServerBuild build = await db.Builds.SingleAsync(TestContext.Current.CancellationToken);
        build.IsExtracted.ShouldBeFalse();
        File.Exists(_environment.Cache.GetP4dPath(build)).ShouldBeFalse();
        _environment.Archive.Exists(build).ShouldBeTrue();
    }

    private async Task SavePolicyAsync(BuildArchivePolicy policy)
    {
        await using DaemonDbContext db = _environment.DbContextFactory.CreateDbContext();
        db.BuildArchivePolicies.Add(policy);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountBuildsAsync()
    {
        await using DaemonDbContext db = _environment.DbContextFactory.CreateDbContext();
        return await db.Builds.CountAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
