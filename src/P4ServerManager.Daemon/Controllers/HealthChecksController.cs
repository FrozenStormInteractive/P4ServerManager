using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.HealthChecks;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The health checks of an instance: runs of <c>p4d -xv</c> and <c>p4d -xx</c>, kept as reports until they are deleted.
/// The health of an instance is told, for each check, by the latest report that ended <c>Healthy</c> or
/// <c>ProblemsFound</c> and ran this check. Deleting such a report only hides it, until a later check replaces it.
/// </summary>
[ApiController]
[Route("api/instances/{instanceId:guid}/health-checks")]
[Produces("application/json")]
[Tags("Health checks")]
public sealed class HealthChecksController(
    DaemonDbContext db,
    HealthCheckRunner runner) : ControllerBase
{
    /// <summary>
    /// Lists the health checks of an instance, with what they found. It includes the reports deleted while they still
    /// tell the health of the instance, marked <c>reportHidden</c>: clients leave them out of the list of reports.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The runs, the latest first.</response>
    /// <response code="404">No instance has this identifier.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<HealthCheckRunDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<IReadOnlyList<HealthCheckRunDto>>> GetHealthChecks(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        if (!await db.Instances.AnyAsync(i => i.Id == instanceId, cancellationToken))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }

        List<HealthCheckRun> runs = await ReadRunsAsync(instanceId, cancellationToken);

        // A run saved as running that the runner no longer knows ended in the meantime: its end is saved by now.
        if (runs.Any(r => r.Status == HealthCheckStatus.Running && runner.GetActive(instanceId, r.Id) is null))
        {
            runs = await ReadRunsAsync(instanceId, cancellationToken);
        }

        // SQLite cannot sort on a DateTimeOffset.
        return runs
            .OrderByDescending(r => r.StartedAt)
            .Select(r => runner.GetActive(instanceId, r.Id) ?? r.ToDto())
            .ToList();
    }

    /// <summary>
    /// Gets a health check of an instance, with its progress while it runs.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="id">Identifier of the run.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The run.</response>
    /// <response code="404">The instance has no such run.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<HealthCheckRunDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<HealthCheckRunDto>> GetHealthCheck(
        Guid instanceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (runner.GetActive(instanceId, id) is { } active)
        {
            return active;
        }

        HealthCheckRun? run = await db.HealthCheckRuns
            .AsNoTracking()
            .Include(r => r.Findings.OrderBy(f => f.Id))
            .FirstOrDefaultAsync(r => r.InstanceId == instanceId && r.Id == id, cancellationToken);
        return run is null ? Problem(statusCode: StatusCodes.Status404NotFound) : run.ToDto();
    }

    /// <summary>
    /// Begins a health check of an instance. It runs in the background: it stops p4d if it runs, which disconnects its
    /// users, checks the database, then starts p4d again if it ran, whatever the outcome. On a large database this can
    /// take hours: prefer a quiet time. The instance is <c>CheckingHealth</c> meanwhile, and nothing can start it. Follow
    /// it with <c>GET /api/instances/{instanceId}/health-checks/{id}</c> or the
    /// <c>HealthCheckUpdated</c> event of the <c>/hubs/instances</c> hub. The checks do not change the database, and no
    /// repair is attempted.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="request">The checks, checked by <see cref="StartHealthCheckRequestValidator"/>.</param>
    /// <param name="supervisor">Begins the run while it holds the instance.</param>
    /// <param name="cancellationToken">Aborts the request. Once begun, only a cancellation stops the run.</param>
    /// <response code="202">The check has begun.</response>
    /// <response code="400">No check is asked for.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">The instance is not ready, or another operation is in progress, such as a check.</response>
    [HttpPost]
    [ProducesResponseType<HealthCheckRunDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<HealthCheckRunDto>> StartHealthCheck(
        Guid instanceId,
        StartHealthCheckRequest request,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        try
        {
            HealthCheckRunDto run = await supervisor.StartHealthCheckAsync(instanceId, request.Checks, cancellationToken);
            return AcceptedAtAction(nameof(GetHealthCheck), new { instanceId, id = run.Id }, run);
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
    /// Stops a running health check. It ends shortly after, as <c>Canceled</c>, and keeps what it found until then. p4d
    /// is started again if it ran.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="id">Identifier of the run.</param>
    /// <response code="202">The run is stopping.</response>
    /// <response code="409">The run has ended, or the instance has no such run.</response>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult> CancelHealthCheck(Guid instanceId, Guid id)
    {
        return await runner.CancelAsync(instanceId, id)
            ? Accepted()
            : Problem("This health check is not running.", statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Downloads the fix journal of a health check: what <c>p4d -xx</c> would change to make the tables consistent. Do
    /// not replay it without the advice of Perforce Support.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="id">Identifier of the run.</param>
    /// <param name="paths">Paths of the data directory.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The journal.</response>
    /// <response code="404">The instance has no such run, or the run kept no fix journal.</response>
    [HttpGet("{id:guid}/fix-journal")]
    [Produces("application/octet-stream", "application/problem+json")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK, "application/octet-stream")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult> GetFixJournal(
        Guid instanceId,
        Guid id,
        [FromServices] DaemonPaths paths,
        CancellationToken cancellationToken)
    {
        bool hasFixJournal = await db.HealthCheckRuns
            .AnyAsync(r => r.InstanceId == instanceId && r.Id == id && r.HasFixJournal, cancellationToken);
        string path = paths.GetHealthCheckFixJournalPath(instanceId, id);
        return hasFixJournal && System.IO.File.Exists(path)
            ? PhysicalFile(path, "application/octet-stream", "jnl.fix")
            : Problem(statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Deletes the report of a health check, with its fix journal. A report that tells the health of the instance is
    /// only hidden: it stays until a check that runs to its end replaces it, so that the instance keeps its health.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="id">Identifier of the run.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="204">The report is deleted, or hidden.</response>
    /// <response code="404">The instance has no such run, or it is already deleted.</response>
    /// <response code="409">The run has not ended.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult> DeleteHealthCheck(
        Guid instanceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (runner.GetActive(instanceId, id) is not null)
        {
            return Problem("This health check is running. Cancel it first.", statusCode: StatusCodes.Status409Conflict);
        }

        return await runner.DeleteReportAsync(instanceId, id, cancellationToken)
            ? NoContent()
            : Problem(statusCode: StatusCodes.Status404NotFound);
    }

    private Task<List<HealthCheckRun>> ReadRunsAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return db.HealthCheckRuns
            .AsNoTracking()
            .Include(r => r.Findings.OrderBy(f => f.Id))
            .Where(r => r.InstanceId == instanceId)
            .ToListAsync(cancellationToken);
    }
}
