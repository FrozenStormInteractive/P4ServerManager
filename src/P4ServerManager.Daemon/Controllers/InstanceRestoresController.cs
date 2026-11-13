using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Restore;
using P4ServerManager.Daemon.Services.Instances;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The restores of backup points over the data of an instance. A restore stops p4d, keeps the current data aside,
/// rebuilds the database from the checkpoint and the journals of the point, puts its archives back, checks the
/// database, starts p4d and verifies the archives. The previous data is deleted once the restore succeeds, and put
/// back if it fails.
/// </summary>
[ApiController]
[Route("api/instances/{instanceId:guid}/restores")]
[Produces("application/json")]
[Tags("Backups")]
public sealed class InstanceRestoresController(DaemonDbContext db) : ControllerBase
{
    /// <summary>
    /// Lists the restores of an instance, including the one that made it from a backup point.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The restores, newest first.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RestoreRunDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RestoreRunDto>>> GetRestores(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        List<RestoreRun> runs = await db.RestoreRuns
            .AsNoTracking()
            .Where(r => r.InstanceId == instanceId)
            .ToListAsync(cancellationToken);

        // SQLite cannot sort on a DateTimeOffset.
        return runs.OrderByDescending(r => r.StartedAt).Select(r => r.ToDto()).ToList();
    }

    /// <summary>
    /// Gets a restore of an instance.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="id">Identifier of the restore.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The restore.</response>
    /// <response code="404">The instance has no such restore.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<RestoreRunDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<RestoreRunDto>> GetRestore(
        Guid instanceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        RestoreRun? run = await db.RestoreRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.InstanceId == instanceId, cancellationToken);
        return run is null ? Problem(statusCode: StatusCodes.Status404NotFound) : run.ToDto();
    }

    /// <summary>
    /// Begins to replace the data of an instance with a backup point: one of its own, or one of another instance with
    /// the same case handling, made with a p4d build no newer than its own. It runs in the background: the instance is
    /// <c>Restoring</c> meanwhile, and its operation tells the progress. The passwords of the restored server are the
    /// ones of the point: the stored super user is checked again afterwards.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="request">The point, checked by <see cref="RestoreBackupRequestValidator"/>.</param>
    /// <param name="supervisor">Runs the restore.</param>
    /// <param name="cancellationToken">Aborts the wait for the instance to be free.</param>
    /// <response code="202">The restore has begun.</response>
    /// <response code="400">The request is not valid.</response>
    /// <response code="404">The instance or the destination does not exist.</response>
    /// <response code="409">The instance is not ready, or another operation is in progress.</response>
    [HttpPost]
    [ProducesResponseType<RestoreRunDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<RestoreRunDto>> StartRestore(
        Guid instanceId,
        RestoreBackupRequest request,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        try
        {
            RestoreRun run = await supervisor.StartRestoreAsync(
                instanceId,
                request.DestinationId,
                request.SnapshotKey,
                cancellationToken);
            return AcceptedAtAction(nameof(GetRestore), new { instanceId, id = run.Id }, run.ToDto());
        }
        catch (KeyNotFoundException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (InstanceConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Stops a running restore. Before the server starts on the restored data, the restore ends as <c>Canceled</c>
    /// and the previous data comes back. During the verification of the archives, only the verification stops.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="id">Identifier of the restore.</param>
    /// <param name="supervisor">Holds the restores in progress.</param>
    /// <response code="202">The restore is stopping.</response>
    /// <response code="409">The restore has ended, or the instance has no such restore.</response>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult> CancelRestore(Guid instanceId, Guid id, [FromServices] InstanceSupervisor supervisor)
    {
        return await supervisor.CancelRestoreAsync(instanceId, id)
            ? Accepted()
            : Problem("This restore is not running.", statusCode: StatusCodes.Status409Conflict);
    }
}
