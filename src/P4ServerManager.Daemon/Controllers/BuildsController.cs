using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Builds;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The p4d builds of the archive. Each one is kept compressed, and extracted while an instance uses it.
/// </summary>
[ApiController]
[Route("api/p4d/builds")]
[Produces("application/json")]
[Tags("p4d")]
public sealed class BuildsController(
    DaemonDbContext db,
    P4ServerPlatformDefinition platformDefinition,
    BuildNotifier notifier) : ControllerBase
{
    /// <summary>
    /// Largest upload accepted by the import: p4d and p4 together weigh well under it.
    /// </summary>
    private const long MaxImportSize = 1L << 30;

    /// <summary>
    /// Lists the builds of the archive for this platform.
    /// </summary>
    /// <response code="200">The builds, newest first.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<BuildDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<BuildDto>> GetBuilds(CancellationToken cancellationToken)
    {
        List<P4ServerBuild> builds = await db.Builds
            .AsNoTracking()
            .Include(b => b.Instances)
            .Where(b => b.Platform == platformDefinition.Name)
            .ToListAsync(cancellationToken);
        return builds
            .OrderByDescending(b => b, Comparer<P4ServerBuild>.Create(P4ServerBuildNumber.Compare))
            .Select(b => b.ToDto())
            .ToList();
    }

    /// <summary>
    /// Gets a build.
    /// </summary>
    /// <param name="id">Identifier of the build.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The build.</response>
    /// <response code="404">No build has this identifier.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<BuildDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BuildDto>> GetBuild(Guid id, CancellationToken cancellationToken)
    {
        P4ServerBuild? build = await db.Builds
            .AsNoTracking()
            .Include(b => b.Instances)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        return build is null ? Problem(statusCode: StatusCodes.Status404NotFound) : build.ToDto();
    }

    /// <summary>
    /// Fetches the current build of a release from Perforce into the archive, unless the archive has it already. The
    /// fetch joins a queue and runs in the background: follow it in the <c>fetches</c> of
    /// <c>GET /api/p4d/archive/status</c> or of the <c>ArchiveStatusChanged</c> event of the <c>/hubs/builds</c> hub,
    /// whose <c>lastDownloadError</c> tells a failure. The new build appears through the <c>BuildUpdated</c> event.
    /// </summary>
    /// <param name="request">The release, checked by <see cref="DownloadBuildRequestValidator"/>.</param>
    /// <param name="worker">Runs the fetch.</param>
    /// <response code="202">The fetch is queued.</response>
    /// <response code="400">The release is not a release name.</response>
    /// <response code="409">The release is already being fetched.</response>
    [HttpPost("download")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public IActionResult DownloadBuild(DownloadBuildRequest request, [FromServices] BuildArchiveWorker worker)
    {
        // Checked by the validator.
        P4ServerRelease.TryParse(request.Release, out P4ServerRelease release);
        if (!worker.RequestDownload(release))
        {
            return Problem($"{release} is already being fetched.", statusCode: StatusCodes.Status409Conflict);
        }
        return Accepted();
    }

    /// <summary>
    /// Imports a build into the archive, from p4d and p4, or from the server package of Perforce
    /// (<c>helix-core-server.tgz</c>). The daemon runs <c>p4d -V</c> to read its version and build: p4d must be for
    /// this platform. The build is compressed before the response, which may take a minute.
    /// </summary>
    /// <param name="form">The files, checked by <see cref="ImportBuildFormValidator"/>.</param>
    /// <param name="importer">Imports the files.</param>
    /// <param name="cancellationToken">Aborts the import.</param>
    /// <response code="201">The build is in the archive.</response>
    /// <response code="400">The files are missing, or are not a p4d and a p4 for this platform.</response>
    /// <response code="409">The archive already has this build.</response>
    [HttpPost("import")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxImportSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImportSize)]
    [ProducesResponseType<BuildDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<BuildDto>> ImportBuild(
        [FromForm] ImportBuildForm form,
        [FromServices] BuildImporter importer,
        CancellationToken cancellationToken)
    {
        P4ServerBuild build;
        try
        {
            if (form.Package is { } package)
            {
                await using Stream stream = package.OpenReadStream();
                build = await importer.ImportPackageAsync(stream, cancellationToken);
            }
            else
            {
                // Checked by the validator.
                await using Stream p4d = form.P4d!.OpenReadStream();
                await using Stream p4 = form.P4!.OpenReadStream();
                build = await importer.ImportFilesAsync(p4d, p4, cancellationToken);
            }
        }
        catch (P4ServerBuildException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (P4ServerBuildConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }

        P4ServerBuild imported = await db.Builds
            .AsNoTracking()
            .Include(b => b.Instances)
            .FirstAsync(b => b.Id == build.Id, cancellationToken);
        return CreatedAtAction(nameof(GetBuild), new { id = build.Id }, imported.ToDto());
    }

    /// <summary>
    /// Changes the settings of a build. The settings left out are kept.
    /// </summary>
    /// <param name="id">Identifier of the build.</param>
    /// <param name="request">Settings to change, checked by <see cref="UpdateBuildRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The build with its new settings.</response>
    /// <response code="400">No setting is given.</response>
    /// <response code="404">No build has this identifier.</response>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType<BuildDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BuildDto>> UpdateBuild(
        Guid id,
        UpdateBuildRequest request,
        CancellationToken cancellationToken)
    {
        P4ServerBuild? build = await db.Builds
            .Include(b => b.Instances)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (build is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }

        if (request.Protected is bool isProtected)
        {
            build.Protected = isProtected;
        }
        await db.SaveChangesAsync(cancellationToken);
        await notifier.BuildChangedAsync(build.Id);
        return build.ToDto();
    }

    /// <summary>
    /// Removes a build: its archive and its extracted files. A build that an instance uses cannot be removed.
    /// </summary>
    /// <param name="id">Identifier of the build.</param>
    /// <param name="buildCache">Removes the build.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="204">The build is removed.</response>
    /// <response code="404">No build has this identifier.</response>
    /// <response code="409">An instance uses the build.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> DeleteBuild(
        Guid id,
        [FromServices] P4ServerBuildCache buildCache,
        CancellationToken cancellationToken)
    {
        try
        {
            await buildCache.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        catch (P4ServerBuildConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
