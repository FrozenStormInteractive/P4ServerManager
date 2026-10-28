using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The p4d builds that instances can run.
/// </summary>
[ApiController]
[Route("api/p4d")]
[Produces("application/json")]
[Tags("p4d")]
public sealed class P4dController(
    IP4ServerReleaseCatalog releaseCatalog,
    DaemonDbContext db,
    P4ServerPlatformDefinition platformDefinition) : ControllerBase
{
    /// <summary>
    /// Lists the p4d releases that can be installed on this platform: those of the Perforce FTP, and those kept in the
    /// archive, with the builds the daemon knows. When the Perforce FTP cannot be read, only the archived releases are
    /// listed.
    /// </summary>
    /// <response code="200">The releases, newest first.</response>
    /// <response code="502">The Perforce FTP could not be read, and the archive is empty.</response>
    [HttpGet("versions")]
    [ProducesResponseType<IReadOnlyList<P4ServerVersionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<IReadOnlyList<P4ServerVersionDto>>> GetVersions(CancellationToken cancellationToken)
    {
        List<P4ServerBuild> known = await db.Builds
            .AsNoTracking()
            .Where(b => b.Platform == platformDefinition.Name)
            .ToListAsync(cancellationToken);

        IReadOnlyList<P4ServerRelease> published;
        try
        {
            published = await releaseCatalog.GetReleasesAsync(cancellationToken);
        }
        catch (HttpRequestException) when (known.Count > 0)
        {
            published = [];
        }
        // Otherwise, a Perforce FTP that cannot be read is answered by PerforceFtpExceptionFilter.

        IEnumerable<P4ServerRelease> archived = known
            .Select(b => P4ServerRelease.TryParse(b.Release, out P4ServerRelease release) ? release : (P4ServerRelease?)null)
            .OfType<P4ServerRelease>();

        return published
            .Concat(archived)
            .Distinct()
            .OrderDescending()
            .Select(release => new P4ServerVersionDto(
                release.Name,
                release.Version,
                known
                    .Where(b => b.Release == release.Name)
                    .OrderByDescending(b => P4ServerBuildNumber.Parse(b.BuildNumber))
                    .Select(b => new CachedBuildDto(b.BuildNumber, b.DownloadedAt, b.IsExtracted))
                    .ToList()))
            .ToList();
    }
}
