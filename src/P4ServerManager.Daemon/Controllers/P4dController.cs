using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Contracts;
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
    /// Lists the p4d releases available for this platform on the Perforce FTP, with the builds already cached.
    /// </summary>
    /// <response code="200">The releases, newest first.</response>
    /// <response code="502">The Perforce FTP could not be read.</response>
    [HttpGet("versions")]
    [ProducesResponseType<IReadOnlyList<P4ServerVersionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<IReadOnlyList<P4ServerVersionDto>>> GetVersions(CancellationToken cancellationToken)
    {
        IReadOnlyList<P4ServerRelease> releases;
        try
        {
            releases = await releaseCatalog.GetReleasesAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return Problem(
                "The list of p4d releases could not be read from the Perforce FTP.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        List<P4ServerBuild> cached = await db.Builds
            .AsNoTracking()
            .Where(b => b.Platform == platformDefinition.Name)
            .ToListAsync(cancellationToken);

        return releases
            .Select(release => new P4ServerVersionDto(
                release.Name,
                release.Version,
                cached
                    .Where(b => b.Release == release.Name)
                    .OrderByDescending(b => b.DownloadedAt)
                    .Select(b => new CachedBuildDto(b.BuildNumber, b.DownloadedAt))
                    .ToList()))
            .ToList();
    }
}
