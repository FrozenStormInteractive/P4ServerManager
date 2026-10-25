using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The p4d servers managed by the daemon.
/// </summary>
[ApiController]
[Route("api/instances")]
[Produces("application/json")]
[Tags("Instances")]
public sealed class InstancesController(
    DaemonDbContext db,
    InstanceRuntimeRegistry runtimeRegistry,
    OperationTracker operationTracker,
    InstanceNotifier notifier,
    TimeProvider timeProvider) : ControllerBase
{
    /// <summary>
    /// Lists the instances.
    /// </summary>
    /// <response code="200">The instances, by name.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<InstanceDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<InstanceDto>> GetInstances(CancellationToken cancellationToken)
    {
        List<P4ServerInstance> instances = await db.Instances
            .AsNoTracking()
            .Include(i => i.Build)
            .OrderBy(i => i.Name)
            .ToListAsync(cancellationToken);
        return instances.Select(ToDto).ToList();
    }

    /// <summary>
    /// Gets an instance.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The instance.</response>
    /// <response code="404">No instance has this identifier.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<InstanceDto>> GetInstance(Guid id, CancellationToken cancellationToken)
    {
        P4ServerInstance? instance = await db.Instances
            .AsNoTracking()
            .Include(i => i.Build)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        return instance is null ? Problem(statusCode: StatusCodes.Status404NotFound) : ToDto(instance);
    }

    /// <summary>
    /// Creates an instance. The provisioning (download of p4d, initialization, start, super user) runs in the
    /// background: follow it with <c>GET /api/instances/{id}/operation</c> or the <c>OperationProgress</c> event
    /// of the <c>/hubs/instances</c> hub.
    /// </summary>
    /// <param name="request">Settings of the instance, checked by <see cref="CreateInstanceRequestValidator"/>.</param>
    /// <param name="queue">Queue of the provisionings.</param>
    /// <param name="paths">Paths of the data directory.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="202">The instance is created and its provisioning has started.</response>
    /// <response code="400">The settings are not valid. The errors are keyed by field.</response>
    /// <response code="502">The list of p4d releases could not be read from the Perforce FTP.</response>
    [HttpPost]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<InstanceDto>> CreateInstance(
        CreateInstanceRequest request,
        [FromServices] ProvisioningQueue queue,
        [FromServices] DaemonPaths paths,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        P4ServerInstance instance = new()
        {
            Id = id,
            Name = request.Name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Port = request.Port,
            RootPath = string.IsNullOrWhiteSpace(request.RootPath)
                ? paths.GetDefaultRootPath(id)
                : Path.GetFullPath(request.RootPath),
            Release = request.Release,
            CaseSensitive = request.CaseSensitive,
            Unicode = request.Unicode,
            AutoStart = request.AutoStart,
            State = P4ServerInstanceState.Provisioning,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.Instances.Add(instance);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request took the name or the port in the meantime.
            return Problem(
                "Another instance already uses this name or port.",
                statusCode: StatusCodes.Status409Conflict);
        }

        SuperUserCredentials superUser = new(request.SuperUser.Name, request.SuperUser.Password);
        await queue.EnqueueAsync(new ProvisioningRequest(id, ProvisioningKind.Create, superUser), cancellationToken);
        await notifier.InstanceChangedAsync(instance);

        return AcceptedAtAction(nameof(GetInstance), new { id }, ToDto(instance));
    }

    /// <summary>
    /// Changes the settings of an instance that can change after its creation. The settings left out are kept. The
    /// change takes effect without a restart of p4d.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="request">Settings to change, checked by <see cref="UpdateInstanceRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The instance with its new settings.</response>
    /// <response code="400">No setting is given, or one is not valid. The errors are keyed by field.</response>
    /// <response code="404">No instance has this identifier.</response>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<InstanceDto>> UpdateInstance(
        Guid id,
        UpdateInstanceRequest request,
        CancellationToken cancellationToken)
    {
        P4ServerInstance? instance = await db.Instances
            .Include(i => i.Build)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (instance is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }

        if (request.AutoStart is bool autoStart)
        {
            instance.AutoStart = autoStart;
        }

        // Only the changed columns are written: an operation running on the instance keeps its own changes.
        await db.SaveChangesAsync(cancellationToken);
        await notifier.InstanceChangedAsync(instance);
        return ToDto(instance);
    }

    /// <summary>
    /// Gets the progress of the last long operation on an instance, such as its provisioning.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <response code="200">The operation, its steps and their status.</response>
    /// <response code="404">The daemon has run no operation on this instance since it started.</response>
    [HttpGet("{id:guid}/operation")]
    [ProducesResponseType<OperationSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public ActionResult<OperationSnapshot> GetOperation(Guid id)
    {
        OperationSnapshot? snapshot = operationTracker.Get(id);
        return snapshot is null ? Problem(statusCode: StatusCodes.Status404NotFound) : snapshot;
    }

    /// <summary>
    /// Starts the p4d of an instance and waits until it accepts connections. The instance is <c>Starting</c> meanwhile.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="supervisor">Starts the process.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the operation runs to its end: only the shutdown of the daemon cancels it.
    /// </param>
    /// <response code="202">p4d runs.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">The instance is not ready, already runs, or another operation is in progress.</response>
    /// <response code="500">p4d did not start.</response>
    [HttpPost("{id:guid}/start")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<ActionResult<InstanceDto>> StartInstance(
        Guid id,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        return RunOperationAsync(() => supervisor.StartInstanceAsync(id, cancellationToken));
    }

    /// <summary>
    /// Stops the p4d of an instance and waits until it exits. The instance is <c>Stopping</c> meanwhile.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="supervisor">Stops the process.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the operation runs to its end: only the shutdown of the daemon cancels it.
    /// </param>
    /// <response code="202">p4d is stopped.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">The instance is not ready, does not run, or another operation is in progress.</response>
    /// <response code="500">p4d did not stop.</response>
    [HttpPost("{id:guid}/stop")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<ActionResult<InstanceDto>> StopInstance(
        Guid id,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        return RunOperationAsync(() => supervisor.StopInstanceAsync(id, cancellationToken));
    }

    /// <summary>
    /// Restarts the p4d of an instance: stops it if it runs, then starts it and waits until it accepts connections.
    /// The instance is <c>Restarting</c> meanwhile.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="supervisor">Restarts the process.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the operation runs to its end: only the shutdown of the daemon cancels it.
    /// </param>
    /// <response code="202">p4d runs again.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">The instance is not ready, or another operation is in progress.</response>
    /// <response code="500">p4d did not stop, or did not start again.</response>
    [HttpPost("{id:guid}/restart")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<ActionResult<InstanceDto>> RestartInstance(
        Guid id,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        return RunOperationAsync(() => supervisor.RestartInstanceAsync(id, cancellationToken));
    }

    /// <summary>
    /// Deletes an instance: stops its p4d if it runs, then removes it from the daemon. The instance is
    /// <c>Deleting</c> meanwhile, and the <c>InstanceDeleted</c> event of the <c>/hubs/instances</c> hub tells the
    /// clients once it is gone. A failed instance can be deleted too.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="deleteFiles">
    /// Also deletes the files of the instance in the data directory: its logs and, when the daemon chose it, its root
    /// with the depots. A root directory given at creation is always kept. False by default: the files stay on disk.
    /// </param>
    /// <param name="supervisor">Stops the process and deletes the instance.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the operation runs to its end: only the shutdown of the daemon cancels it.
    /// </param>
    /// <response code="204">The instance is deleted.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">The instance is being provisioned, or another operation is in progress.</response>
    /// <response code="500">p4d did not stop, or the files could not be deleted. The instance is kept.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<ActionResult> DeleteInstance(
        Guid id,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken,
        [FromQuery] bool deleteFiles = false)
    {
        return HandleOperationErrorsAsync(async () =>
        {
            await supervisor.DeleteInstanceAsync(id, deleteFiles, cancellationToken);
            return NoContent();
        });
    }

    private async Task<ActionResult<InstanceDto>> RunOperationAsync(Func<Task<P4ServerInstance>> operation)
    {
        return await HandleOperationErrorsAsync(async () => Accepted(ToDto(await operation())));
    }

    private async Task<ActionResult> HandleOperationErrorsAsync(Func<Task<ActionResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        catch (InstanceConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (InstanceOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private InstanceDto ToDto(P4ServerInstance instance)
    {
        return InstanceDto.From(instance, runtimeRegistry.Get(instance.Id));
    }
}
