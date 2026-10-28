using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Builds;

/// <summary>
/// Looks after the build archive in the background. At startup it brings the builds in line with the disk; then, every
/// few minutes or on request, it archives the builds that are only extracted, deletes the extracted files of the
/// builds no instance uses, fetches the builds asked for, checks Perforce when the <see cref="BuildArchivePolicy"/>
/// says it is time, and applies the retention.
/// </summary>
public sealed class BuildArchiveWorker(
    P4ServerBuildCache buildCache,
    BuildArchiveSynchronizer synchronizer,
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    BuildNotifier notifier,
    TimeProvider timeProvider,
    ILogger<BuildArchiveWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    private readonly Channel<Request> requests = Channel.CreateUnbounded<Request>();

    // One pass at a time, even when a caller other than the loop runs one.
    private readonly SemaphoreSlim passLock = new(1, 1);

    // The builds being fetched, in their order: one per release.
    private readonly Lock fetchesLock = new();
    private readonly List<BuildFetchDto> fetches = [];

    // 1 while a check is queued or running: a second one is refused rather than run right after.
    private int checkPending;

    private volatile bool running;
    private volatile bool checking;
    private volatile Activity? activity;
    private volatile string? lastDownloadError;

    // What the status tells of the policy, kept so that each progress report is pushed without reading the database.
    private volatile PolicyStatus? policyStatus;

    /// <summary>
    /// Asks for a check of Perforce now, whether the automatic download is on or not.
    /// </summary>
    /// <returns>False when a check is already queued or running: nothing more is queued.</returns>
    public bool RequestCheck()
    {
        if (Interlocked.Exchange(ref checkPending, 1) == 1)
        {
            return false;
        }
        requests.Writer.TryWrite(new Request(RequestKind.Check, null));
        _ = PushStatusAsync();
        return true;
    }

    /// <summary>
    /// Asks for the current build of a release to be fetched from Perforce into the archive. It waits in the queue of
    /// the fetches until its turn.
    /// </summary>
    /// <returns>False when the release is already being fetched: nothing more is queued.</returns>
    public bool RequestDownload(P4ServerRelease release)
    {
        lock (fetchesLock)
        {
            if (fetches.Any(f => f.Release == release.Name))
            {
                return false;
            }
            fetches.Add(new BuildFetchDto(release.Name, BuildFetchState.Queued, null, null, null));
        }
        requests.Writer.TryWrite(new Request(RequestKind.Download, release));
        _ = PushStatusAsync();
        return true;
    }

    /// <summary>
    /// Asks for the retention to be applied again, once the policy changed.
    /// </summary>
    public void RequestRetention()
    {
        requests.Writer.TryWrite(new Request(RequestKind.Retention, null));
    }

    public async Task<BuildArchiveStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        BuildArchivePolicy policy = await db.GetBuildArchivePolicyAsync(cancellationToken);
        policyStatus = ReadPolicyStatus(policy);
        return GetStatus();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await buildCache.ReconcileAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await WaitAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The daemon stops.
        }
    }

    /// <summary>
    /// Runs the pending requests, the maintenance, and the check when it is due.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await passLock.WaitAsync(cancellationToken);
        try
        {
            running = true;
            await RunPassAsync(cancellationToken);
        }
        finally
        {
            running = false;
            passLock.Release();
        }
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        bool checkRequested = false;
        bool retentionRequested = false;
        List<P4ServerRelease> downloads = [];
        while (requests.Reader.TryRead(out Request? request))
        {
            switch (request.Kind)
            {
                case RequestKind.Check:
                    checkRequested = true;
                    break;
                case RequestKind.Retention:
                    retentionRequested = true;
                    break;
                case RequestKind.Download when !downloads.Contains(request.Release!.Value):
                    downloads.Add(request.Release.Value);
                    break;
            }
        }

        try
        {
            await MaintainAsync(cancellationToken);

            foreach (P4ServerRelease release in downloads)
            {
                await DownloadAsync(release, cancellationToken);
            }

            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            BuildArchivePolicy policy = await db.GetBuildArchivePolicyAsync(cancellationToken);
            policyStatus = ReadPolicyStatus(policy);
            if (checkRequested || IsCheckDue(policy))
            {
                // A check asked for now is refused until this one is over.
                Interlocked.Exchange(ref checkPending, 1);
                checking = true;
                await SetActivityAsync(new Activity("Checking Perforce for new builds", null));
                await CheckAsync(db, policy, cancellationToken);
                retentionRequested = true;
            }
            if (retentionRequested || downloads.Count > 0)
            {
                await synchronizer.ApplyRetentionAsync(policy, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The maintenance of the build archive failed");
        }
        finally
        {
            Interlocked.Exchange(ref checkPending, 0);
            checking = false;
            running = false;
            lock (fetchesLock)
            {
                // Only the fetches asked for during the pass are left: they wait for the next one. Those of the pass
                // that a failure skipped go too, their request is spent.
                fetches.RemoveAll(f => f.State != BuildFetchState.Queued || downloads.Any(d => d.Name == f.Release));
            }
            await SetActivityAsync(null);
        }
    }

    private async Task MaintainAsync(CancellationToken cancellationToken)
    {
        List<P4ServerBuild> builds;
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            builds = await db.Builds.AsNoTracking().ToListAsync(cancellationToken);
        }

        foreach (P4ServerBuild build in builds.Where(b => b.ArchiveSize is null && b.IsExtracted))
        {
            await SetActivityAsync(
                new Activity($"Compressing p4d {build.Version}/{build.BuildNumber} into the archive", null));
            try
            {
                InlineProgress<BuildProgress> progress = new(p => ReportActivity(p.Description, p.Percent));
                await buildCache.ArchiveAsync(build.Id, progress, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not archive p4d {Version}/{Build}", build.Version, build.BuildNumber);
            }
        }

        foreach (P4ServerBuild build in builds.Where(b => b.IsExtracted))
        {
            await buildCache.RemoveExtractedAsync(build.Id, cancellationToken);
        }
    }

    private async Task DownloadAsync(P4ServerRelease release, CancellationToken cancellationToken)
    {
        await SetFetchAsync(new BuildFetchDto(release.Name, BuildFetchState.Checking, null, null, null));
        try
        {
            InlineProgress<BuildProgress> progress = new(ReportFetch);
            (P4ServerBuild Build, bool Downloaded)? result = await buildCache.AcquireAsync(release, progress, cancellationToken);
            lastDownloadError = result is null ? $"{release} has no p4d build for this platform." : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not download the p4d build of {Release}", release);
            lastDownloadError = $"{release}: {exception.Message}";
        }
        finally
        {
            await RemoveFetchAsync(release.Name);
        }
    }

    private async Task CheckAsync(DaemonDbContext db, BuildArchivePolicy policy, CancellationToken cancellationToken)
    {
        logger.LogInformation("Checking the Perforce FTP for new p4d builds");
        string? current = null;
        BuildArchiveCheckResult result = await synchronizer.CheckAsync(
            policy,
            (release, progress) =>
            {
                if (progress is { } p)
                {
                    // The release has a new build: it joins the fetches while it is fetched.
                    ReportFetch(p);
                    return;
                }
                if (current is not null)
                {
                    _ = RemoveFetchAsync(current);
                }
                current = release.Name;
                ReportActivity($"Checking {release}", null);
            },
            cancellationToken);
        if (current is not null)
        {
            await RemoveFetchAsync(current);
        }

        policy.LastCheckAt = timeProvider.GetUtcNow();
        policy.LastCheckError = result.Errors.Count > 0 ? string.Join(Environment.NewLine, result.Errors) : null;
        await db.SaveChangesAsync(cancellationToken);
        policyStatus = ReadPolicyStatus(policy);
        logger.LogInformation(
            "Checked the Perforce FTP: {Count} new p4d builds, {Errors} errors",
            result.Downloaded.Count,
            result.Errors.Count);
    }

    private bool IsCheckDue(BuildArchivePolicy policy)
    {
        return GetNextCheckAt(policy) is { } next && next <= timeProvider.GetUtcNow();
    }

    private DateTimeOffset? GetNextCheckAt(BuildArchivePolicy policy)
    {
        if (!policy.AutoDownload)
        {
            return null;
        }
        return policy.LastCheckAt?.AddHours(policy.CheckIntervalHours) ?? timeProvider.GetUtcNow();
    }

    private PolicyStatus ReadPolicyStatus(BuildArchivePolicy policy)
    {
        return new PolicyStatus(policy.LastCheckAt, policy.LastCheckError, GetNextCheckAt(policy));
    }

    private BuildArchiveStatusDto GetStatus()
    {
        Activity? current = activity;
        PolicyStatus? policy = policyStatus;
        BuildFetchDto[] queue;
        lock (fetchesLock)
        {
            // The fetch under way first, then those waiting.
            queue = [.. fetches.OrderBy(f => f.State == BuildFetchState.Queued)];
        }
        return new BuildArchiveStatusDto(
            running || current is not null || queue.Length > 0,
            current?.Description,
            current?.Percent,
            policy?.LastCheckAt,
            policy?.LastCheckError,
            policy?.NextCheckAt,
            lastDownloadError,
            checking || Volatile.Read(ref checkPending) == 1,
            queue);
    }

    private void ReportActivity(string description, int? percent)
    {
        Activity? current = activity;
        if (current is not null && current.Description == description && current.Percent == percent)
        {
            return;
        }
        // The progress is reported synchronously, from within the download: the push is not awaited.
        _ = SetActivityAsync(new Activity(description, percent));
    }

    private void ReportFetch(BuildProgress progress)
    {
        BuildFetchState state = progress.Stage == BuildProgressStage.Compressing
            ? BuildFetchState.Compressing
            : BuildFetchState.Downloading;
        // Reported synchronously, from within the download: the push is not awaited.
        _ = SetFetchAsync(new BuildFetchDto(progress.Release, state, progress.FileName, progress.Build, progress.Percent));
    }

    private async Task SetFetchAsync(BuildFetchDto fetch)
    {
        lock (fetchesLock)
        {
            int index = fetches.FindIndex(f => f.Release == fetch.Release);
            if (index < 0)
            {
                fetches.Add(fetch);
            }
            else if (fetches[index] == fetch)
            {
                return;
            }
            else
            {
                fetches[index] = fetch;
            }
        }
        await PushStatusAsync();
    }

    private async Task RemoveFetchAsync(string release)
    {
        lock (fetchesLock)
        {
            if (fetches.RemoveAll(f => f.Release == release) == 0)
            {
                return;
            }
        }
        await PushStatusAsync();
    }

    private async Task SetActivityAsync(Activity? next)
    {
        activity = next;
        await PushStatusAsync();
    }

    private async Task PushStatusAsync()
    {
        try
        {
            BuildArchiveStatusDto status = policyStatus is null ? await GetStatusAsync(CancellationToken.None) : GetStatus();
            await notifier.ArchiveStatusChangedAsync(status);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the status of the build archive");
        }
    }

    private async Task WaitAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource timeout = new(TickInterval, timeProvider);
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        try
        {
            await requests.Reader.WaitToReadAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The next tick.
        }
    }

    private sealed record Activity(string Description, int? Percent);

    private sealed record PolicyStatus(DateTimeOffset? LastCheckAt, string? LastCheckError, DateTimeOffset? NextCheckAt);

    private enum RequestKind
    {
        Check,
        Download,
        Retention,
    }

    private sealed record Request(RequestKind Kind, P4ServerRelease? Release);
}
