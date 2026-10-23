using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using P4ServerManager.Daemon.Options;

namespace P4ServerManager.Daemon.Services.Perforce;

/// <summary>
/// Lists the releases of the Perforce FTP from the configured minimum, keeping those that publish p4d and p4 for this
/// platform. The list is cached for an hour.
/// </summary>
public sealed class P4ServerReleaseCatalog(
    PerforceFtpClient ftpClient,
    P4ServerPlatformDefinition platformDefinition,
    IMemoryCache cache,
    IOptions<DaemonOptions> options) : IP4ServerReleaseCatalog
{
    private const string CacheKey = "p4d-releases";
    private const int MaxConcurrentRequests = 8;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public async Task<IReadOnlyList<P4ServerRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<P4ServerRelease>? cached) && cached is not null)
        {
            return cached;
        }

        IReadOnlyList<P4ServerRelease> releases = await LoadReleasesAsync(cancellationToken);
        cache.Set(CacheKey, releases, CacheDuration);
        return releases;
    }

    private async Task<IReadOnlyList<P4ServerRelease>> LoadReleasesAsync(CancellationToken cancellationToken)
    {
        P4ServerRelease.TryParse(options.Value.MinimumRelease, out P4ServerRelease minimum);
        List<P4ServerRelease> candidates = (await ftpClient.GetReleasesAsync(cancellationToken))
            .Where(release => release >= minimum)
            .ToList();

        using SemaphoreSlim throttle = new(MaxConcurrentRequests);
        bool[] available = await Task.WhenAll(candidates.Select(async release =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                IReadOnlyDictionary<string, string>? checksums =
                    await ftpClient.GetChecksumsAsync(release, cancellationToken);
                return checksums is not null
                    && checksums.ContainsKey(platformDefinition.P4dFileName)
                    && checksums.ContainsKey(platformDefinition.P4FileName);
            }
            finally
            {
                throttle.Release();
            }
        }));

        return candidates.Where((_, index) => available[index]).ToList();
    }
}

/// <summary>
/// The p4d releases that can be installed on this platform.
/// </summary>
public interface IP4ServerReleaseCatalog
{
    /// <returns>The releases, newest first.</returns>
    Task<IReadOnlyList<P4ServerRelease>> GetReleasesAsync(CancellationToken cancellationToken);
}
