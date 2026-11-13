using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Destinations;
using P4ServerManager.Daemon.Services.Instances;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The backups of an instance: the destinations it backs up to, its backups, and their verification. A backup runs
/// while p4d keeps running. A full backup takes a checkpoint; an incremental one rotates the journal and builds on the
/// latest backup at each destination. Each backup uploads to every destination only what it does not have yet.
/// </summary>
[ApiController]
[Route("api/instances/{instanceId:guid}")]
[Produces("application/json")]
[Tags("Backups")]
public sealed class InstanceBackupsController(DaemonDbContext db) : ControllerBase
{
    /// <summary>
    /// Lists the destinations an instance backs up to.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The destinations.</response>
    /// <response code="404">No instance has this identifier.</response>
    [HttpGet("backup-destinations")]
    [ProducesResponseType<InstanceBackupDestinations>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<InstanceBackupDestinations>> GetBackupDestinations(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        if (!await db.Instances.AnyAsync(i => i.Id == instanceId, cancellationToken))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        return new InstanceBackupDestinations { DestinationIds = await ReadDestinationIdsAsync(instanceId, cancellationToken) };
    }

    /// <summary>
    /// Sets the destinations an instance backs up to. The backups already made stay at the destinations removed.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="request">The destinations, checked by <see cref="InstanceBackupDestinationsValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The destinations of the instance.</response>
    /// <response code="400">A destination is listed twice, or does not exist.</response>
    /// <response code="404">No instance has this identifier.</response>
    [HttpPut("backup-destinations")]
    [ProducesResponseType<InstanceBackupDestinations>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<InstanceBackupDestinations>> SetBackupDestinations(
        Guid instanceId,
        InstanceBackupDestinations request,
        CancellationToken cancellationToken)
    {
        if (!await db.Instances.AnyAsync(i => i.Id == instanceId, cancellationToken))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }

        int known = await db.BackupDestinations.CountAsync(d => request.DestinationIds.Contains(d.Id), cancellationToken);
        if (known != request.DestinationIds.Count)
        {
            ModelState.AddModelError("destinationIds", "A destination does not exist.");
            return ValidationProblem(ModelState);
        }

        List<InstanceBackupDestination> current = await db.InstanceBackupDestinations
            .Where(i => i.InstanceId == instanceId)
            .ToListAsync(cancellationToken);
        db.InstanceBackupDestinations.RemoveRange(current.Where(c => !request.DestinationIds.Contains(c.DestinationId)));
        db.InstanceBackupDestinations.AddRange(request.DestinationIds
            .Where(id => current.All(c => c.DestinationId != id))
            .Select(id => new InstanceBackupDestination { InstanceId = instanceId, DestinationId = id }));
        await db.SaveChangesAsync(cancellationToken);
        return new InstanceBackupDestinations { DestinationIds = await ReadDestinationIdsAsync(instanceId, cancellationToken) };
    }

    /// <summary>
    /// Lists the backups of an instance, with what each one did at each destination.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance. The backups of a deleted instance are still listed.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The backups, the latest first.</response>
    [HttpGet("backups")]
    [ProducesResponseType<IReadOnlyList<BackupRunDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BackupRunDto>>> GetBackups(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        List<BackupRun> runs = await db.BackupRuns
            .AsNoTracking()
            .Include(r => r.Destinations)
            .Where(r => r.InstanceId == instanceId)
            .ToListAsync(cancellationToken);
        // SQLite cannot sort on a DateTimeOffset.
        return runs.OrderByDescending(r => r.StartedAt).Select(r => r.ToDto()).ToList();
    }

    /// <summary>
    /// Gets a backup of an instance.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="id">Identifier of the backup.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The backup.</response>
    /// <response code="404">The instance has no such backup.</response>
    [HttpGet("backups/{id:guid}")]
    [ProducesResponseType<BackupRunDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BackupRunDto>> GetBackup(Guid instanceId, Guid id, CancellationToken cancellationToken)
    {
        BackupRun? run = await db.BackupRuns
            .AsNoTracking()
            .Include(r => r.Destinations)
            .FirstOrDefaultAsync(r => r.InstanceId == instanceId && r.Id == id, cancellationToken);
        return run is null ? Problem(statusCode: StatusCodes.Status404NotFound) : run.ToDto();
    }

    /// <summary>
    /// Begins a backup of an instance to its destinations, or to some of them. It runs in the background while p4d
    /// keeps running: the instance is <c>BackingUp</c> meanwhile, and no other operation can run on it. Follow it with
    /// <c>GET /api/instances/{instanceId}/backups/{id}</c>, or the <c>BackupUpdated</c> and <c>OperationProgress</c>
    /// events of the <c>/hubs/instances</c> hub. An incremental backup needs, at each destination, a backup of the
    /// instance that ends right before the current journal: a destination without one fails, the others go on.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="request">The backup, checked by <see cref="StartBackupRequestValidator"/>.</param>
    /// <param name="supervisor">Begins the backup while it holds the instance.</param>
    /// <param name="cancellationToken">Aborts the request. Once begun, only a cancellation stops the backup.</param>
    /// <response code="202">The backup has begun.</response>
    /// <response code="400">The request is not valid.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">
    /// The instance is not ready, another operation is in progress, it has no backup destination, or a destination asked
    /// for is not one of its own.
    /// </response>
    [HttpPost("backups")]
    [ProducesResponseType<BackupRunDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<BackupRunDto>> StartBackup(
        Guid instanceId,
        StartBackupRequest request,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        try
        {
            BackupRun run = await supervisor.StartBackupAsync(
                instanceId,
                request.Kind,
                request.DestinationIds,
                BackupTrigger.Manual,
                cancellationToken);
            return AcceptedAtAction(nameof(GetBackup), new { instanceId, id = run.Id }, run.ToDto());
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        catch (InstanceConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Stops a running backup. It ends shortly after, as <c>Canceled</c>. The destinations it had not finished keep no
    /// backup point; the objects already uploaded there are reused by the next backup.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="id">Identifier of the backup.</param>
    /// <param name="supervisor">Holds the backups in progress.</param>
    /// <response code="202">The backup is stopping.</response>
    /// <response code="409">The backup has ended, or the instance has no such backup.</response>
    [HttpPost("backups/{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult> CancelBackup(Guid instanceId, Guid id, [FromServices] InstanceSupervisor supervisor)
    {
        return await supervisor.CancelBackupAsync(instanceId, id)
            ? Accepted()
            : Problem("This backup is not running.", statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Begins the verification of a backup at one of its destinations: that every object of the point exists, or, for a
    /// deep verification, that every object decrypts to the content the point tells, which downloads the whole point.
    /// It runs in the background: the instance is <c>VerifyingBackup</c> meanwhile; p4d is not touched. The outcome is
    /// kept with the backup.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="id">Identifier of the backup.</param>
    /// <param name="request">The destination, checked by <see cref="VerifyBackupRequestValidator"/>.</param>
    /// <param name="supervisor">Begins the verification while it holds the instance.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="202">The verification has begun.</response>
    /// <response code="400">The request is not valid.</response>
    /// <response code="404">The instance or the backup does not exist.</response>
    /// <response code="409">
    /// The instance is not ready, another operation is in progress, or the backup wrote no point at the destination.
    /// </response>
    [HttpPost("backups/{id:guid}/verify")]
    [ProducesResponseType<BackupRunDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<BackupRunDto>> VerifyBackup(
        Guid instanceId,
        Guid id,
        VerifyBackupRequest request,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        try
        {
            BackupRun run = await supervisor.StartBackupVerificationAsync(
                instanceId,
                id,
                request.DestinationId,
                request.Deep,
                cancellationToken);
            return AcceptedAtAction(nameof(GetBackup), new { instanceId, id = run.Id }, run.ToDto());
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        catch (InstanceConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private async Task<List<Guid>> ReadDestinationIdsAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return await db.InstanceBackupDestinations
            .Where(i => i.InstanceId == instanceId)
            .OrderBy(i => i.Destination!.Name)
            .Select(i => i.DestinationId)
            .ToListAsync(cancellationToken);
    }
}
