using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Options;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Builds;

/// <summary>
/// Applies the <see cref="BuildArchivePolicy"/>: downloads the builds of the Perforce FTP that the archive lacks, found
/// through the <c>SHA256SUMS</c> of each release, then removes the builds beyond the retention.
/// </summary>
public sealed class BuildArchiveSynchronizer(
    P4ServerBuildCache buildCache,
    PerforceFtpClient ftpClient,
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    P4ServerPlatformDefinition platformDefinition,
    IOptions<DaemonOptions> options,
    ILogger<BuildArchiveSynchronizer> logger)
{
    /// <summary>
    /// Checks every release of the policy for a new build, and downloads it into the archive. A release that fails does
    /// not stop the others.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="report">
    /// Told when the check of a release begins, with no progress, then how the fetch of its new build goes, if any.
    /// </param>
    /// <param name="cancellationToken">Aborts the check.</param>
    /// <returns>The builds downloaded, and the errors, one per release that failed.</returns>
    public async Task<BuildArchiveCheckResult> CheckAsync(
        BuildArchivePolicy policy,
        Action<P4ServerRelease, BuildProgress?> report,
        CancellationToken cancellationToken)
    {
        List<P4ServerBuild> downloaded = [];
        List<string> errors = [];

        IReadOnlyList<P4ServerRelease> releases;
        try
        {
            releases = await GetReleasesAsync(policy, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return new BuildArchiveCheckResult(downloaded, [$"Perforce could not be reached: {exception.Message}"]);
        }

        foreach (P4ServerRelease release in releases)
        {
            report(release, null);
            try
            {
                InlineProgress<BuildProgress> progress = new(p => report(release, p));
                (P4ServerBuild Build, bool Downloaded)? result =
                    await buildCache.AcquireAsync(release, progress, cancellationToken);
                if (result is { Downloaded: true } added)
                {
                    downloaded.Add(added.Build);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not check release {Release} for a new p4d build", release);
                errors.Add($"{release}: {exception.Message}");
            }
        }

        return new BuildArchiveCheckResult(downloaded, errors);
    }

    /// <summary>
    /// Removes the builds of each release beyond the newest <see cref="BuildArchivePolicy.KeepLatestPerRelease"/>,
    /// except those that instances use and the protected ones.
    /// </summary>
    /// <returns>The number of builds removed.</returns>
    public async Task<int> ApplyRetentionAsync(BuildArchivePolicy policy, CancellationToken cancellationToken)
    {
        if (policy.KeepLatestPerRelease is not int keep)
        {
            return 0;
        }

        List<P4ServerBuild> builds;
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            builds = await db.Builds
                .AsNoTracking()
                .Include(b => b.Instances)
                .Where(b => b.Platform == platformDefinition.Name)
                .ToListAsync(cancellationToken);
        }

        IEnumerable<P4ServerBuild> expired = builds
            .GroupBy(b => b.Release)
            .SelectMany(release => release
                .OrderByDescending(b => P4ServerBuildNumber.Parse(b.BuildNumber))
                .Skip(keep))
            .Where(b => !b.Protected && b.Instances.Count == 0);

        int removed = 0;
        foreach (P4ServerBuild build in expired)
        {
            try
            {
                await buildCache.DeleteAsync(build.Id, cancellationToken);
                removed++;
            }
            catch (Exception exception) when (exception is P4ServerBuildConflictException or KeyNotFoundException)
            {
                // Pinned or removed in the meantime.
            }
        }
        return removed;
    }

    private async Task<IReadOnlyList<P4ServerRelease>> GetReleasesAsync(
        BuildArchivePolicy policy,
        CancellationToken cancellationToken)
    {
        if (policy.Scope == BuildArchiveScope.SelectedReleases)
        {
            return policy.Releases
                .Select(name => P4ServerRelease.TryParse(name, out P4ServerRelease release) ? release : (P4ServerRelease?)null)
                .OfType<P4ServerRelease>()
                .Distinct()
                .OrderDescending()
                .ToList();
        }

        // Read anew, not from the cache of the release catalog: a release published within the hour counts.
        P4ServerRelease.TryParse(options.Value.MinimumRelease, out P4ServerRelease minimum);
        return (await ftpClient.GetReleasesAsync(cancellationToken))
            .Where(release => release >= minimum)
            .ToList();
    }
}

/// <param name="Downloaded">Builds added to the archive.</param>
/// <param name="Errors">One message per release that could not be checked.</param>
public sealed record BuildArchiveCheckResult(IReadOnlyList<P4ServerBuild> Downloaded, IReadOnlyList<string> Errors);
