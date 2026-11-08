using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Instances.Upgrade;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;

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
    /// Creates an instance, with recommended configuration: folders, recommended configurables, security level 4, spec
    /// and unload depots, server spec and typemap. The provisioning (download of p4d, initialization, start,
    /// super user, configuration) runs in the background: follow it with <c>GET /api/instances/{id}/operation</c> or
    /// the <c>OperationProgress</c> event of the <c>/hubs/instances</c> hub.
    /// </summary>
    /// <param name="request">Settings of the instance, checked by <see cref="CreateInstanceRequestValidator"/>.</param>
    /// <param name="queue">Queue of the provisionings.</param>
    /// <param name="paths">Paths of the data directory.</param>
    /// <param name="passwordProtector">Encrypts the password of the super user, which the daemon stores.</param>
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
        [FromServices] SuperUserPasswordProtector passwordProtector,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset now = timeProvider.GetUtcNow();
        P4ServerInstance instance = new()
        {
            Id = id,
            Name = request.Name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Port = request.Port,
            Ssl = request.Ssl,
            RootPath = string.IsNullOrWhiteSpace(request.ServerDirectory)
                ? paths.GetDefaultRootPath(id)
                : ServerLayout.GetRootPath(Path.GetFullPath(request.ServerDirectory)),
            Release = request.Release,
            // Checked by the validator. Without a build, the provisioning pins the latest one.
            BuildId = request.Build is null
                ? null
                : await db.Builds
                    .Where(b => b.Release == request.Release && b.BuildNumber == request.Build)
                    .Select(b => (Guid?)b.Id)
                    .FirstOrDefaultAsync(cancellationToken),
            CaseSensitive = request.CaseSensitive,
            Unicode = request.Unicode,
            AutoStart = request.AutoStart,
            State = P4ServerInstanceState.Provisioning,
            // Stored now, checked once the server is provisioned.
            SuperUser = new InstanceSuperUser
            {
                UserName = request.SuperUser.Name,
                ProtectedPassword = passwordProtector.Protect(request.SuperUser.Password),
                Status = SuperUserStatus.Unchecked,
                UpdatedAt = now,
            },
            CreatedAt = now,
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
    /// change takes effect without a restart of p4d. A new name only changes the name in the daemon: the p4d server
    /// keeps its server ID and the prefix of its journals.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="request">Settings to change, checked by <see cref="UpdateInstanceRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The instance with its new settings.</response>
    /// <response code="400">
    /// No setting is given, one is not valid, or another instance has the name. The errors are keyed by field.
    /// </response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">The name or the description is given while an operation is in progress on the instance.</response>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
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

        // An operation in progress pushes its own copy of the instance to the clients: they would see the old texts again.
        bool changesTexts = request.Name is not null || request.Description is not null;
        if (changesTexts
            && (instance.State == P4ServerInstanceState.Provisioning || runtimeRegistry.Get(id).IsTransitional()))
        {
            return Problem(
                "Another operation is in progress on this instance.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (request.Name is string name)
        {
            // The instance can change the case of its own name.
            if (await db.Instances.AnyAsync(i => i.Id != id && i.Name.ToLower() == name.ToLower(), cancellationToken))
            {
                ModelState.AddModelError("name", $"An instance named {name} already exists.");
                return ValidationProblem(ModelState);
            }

            instance.Name = name;
        }

        if (request.Description is string description)
        {
            instance.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }

        if (request.AutoStart is bool autoStart)
        {
            instance.AutoStart = autoStart;
        }

        // Only the changed columns are written: an operation running on the instance keeps its own changes.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request took the name in the meantime.
            return Problem("Another instance already uses this name.", statusCode: StatusCodes.Status409Conflict);
        }

        await notifier.InstanceChangedAsync(instance);
        return ToDto(instance);
    }

    /// <summary>
    /// Gets the progress of the last long operation on an instance, such as its provisioning or its upgrade.
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
    /// Enables the maintenance mode of an instance, and waits until its p4d accepts connections in it (<c>p4d -n</c>). In
    /// maintenance mode, p4d does not enforce the user and file limits of its license, but only allows the commands that
    /// do not need a client workspace: users can no longer sync or submit. A running p4d is restarted, and the instance
    /// is <c>Restarting</c> meanwhile; a stopped one is started, and the instance is <c>Starting</c> meanwhile. The mode
    /// is kept: every later start of p4d, including the auto-start and the one after an upgrade, follows it until the
    /// maintenance mode is disabled with <c>POST /api/instances/{id}/maintenance/disable</c>.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="supervisor">Restarts or starts the process.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the operation runs to its end: only the shutdown of the daemon cancels it.
    /// </param>
    /// <response code="202">p4d runs in maintenance mode.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">
    /// The instance is not ready, another operation is in progress, or p4d already runs in maintenance mode.
    /// </response>
    /// <response code="500">
    /// p4d did not stop, or did not start in maintenance mode. The maintenance mode stays enabled.
    /// </response>
    [HttpPost("{id:guid}/maintenance/enable")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<ActionResult<InstanceDto>> EnableMaintenanceMode(
        Guid id,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        return RunOperationAsync(() => supervisor.EnableMaintenanceModeAsync(id, cancellationToken));
    }

    /// <summary>
    /// Disables the maintenance mode of an instance. A running p4d is restarted in normal mode, and the instance is
    /// <c>Restarting</c> meanwhile; the request waits until p4d accepts connections again. A stopped p4d stays stopped:
    /// its next start is a normal one.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="supervisor">Restarts the process.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the operation runs to its end: only the shutdown of the daemon cancels it.
    /// </param>
    /// <response code="202">The maintenance mode is disabled, and p4d runs in normal mode if it ran.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">
    /// The instance is not ready, another operation is in progress, or its maintenance mode is not enabled.
    /// </response>
    /// <response code="500">
    /// p4d did not stop, or did not start again. The maintenance mode stays disabled.
    /// </response>
    [HttpPost("{id:guid}/maintenance/disable")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<ActionResult<InstanceDto>> DisableMaintenanceMode(
        Guid id,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        return RunOperationAsync(() => supervisor.DisableMaintenanceModeAsync(id, cancellationToken));
    }

    /// <summary>
    /// Upgrades an instance to the latest build of a p4d release, or to an exact build of it from the archive: the
    /// release of the instance, for a newer patch, or a newer release. The upgrade runs in the background: it gets the build, stops p4d if it runs, upgrades the
    /// database, then starts p4d again if it ran. No backup of the database is taken. The instance is
    /// <c>Upgrading</c> meanwhile. Follow it with <c>GET /api/instances/{id}/operation</c> or the
    /// <c>OperationProgress</c> event of the <c>/hubs/instances</c> hub. A failure before the database is upgraded
    /// leaves the instance on its previous build, started again if it ran; the error is kept as its last error.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="request">The release and the build, checked by <see cref="UpgradeInstanceRequestValidator"/>.</param>
    /// <param name="supervisor">Runs the upgrade.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the upgrade runs to its end: only the shutdown of the daemon cancels it, and
    /// never while p4d upgrades the database.
    /// </param>
    /// <response code="202">The upgrade has begun.</response>
    /// <response code="400">The release is not an available p4d release, or the build is not in the archive.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">
    /// The instance is not ready, another operation is in progress, or the release or the build is older than the one
    /// of the instance: a database cannot go back to an older release.
    /// </response>
    /// <response code="502">
    /// The list of p4d releases could not be read from the Perforce FTP, and the archive has no build of the release.
    /// </response>
    [HttpPost("{id:guid}/upgrade")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway, "application/problem+json")]
    public Task<ActionResult<InstanceDto>> UpgradeInstance(
        Guid id,
        UpgradeInstanceRequest request,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        // Checked by the validator.
        P4ServerRelease.TryParse(request.Release, out P4ServerRelease release);
        return RunOperationAsync(() => supervisor.UpgradeInstanceAsync(id, release, request.Build, cancellationToken));
    }

    /// <summary>
    /// Converts an instance to Unicode mode. It cannot be undone. The conversion runs in the background: it stops p4d if
    /// it runs, saves a checkpoint to the folder of the instance, switches the database with <c>p4d -xi</c>, then starts
    /// p4d again if it ran. The instance is <c>ConvertingToUnicode</c> meanwhile. Follow it with
    /// <c>GET /api/instances/{id}/operation</c> or the <c>OperationProgress</c> event of the <c>/hubs/instances</c> hub.
    /// p4d refuses metadata that is not valid UTF-8: the instance then stays as it was, started again if it ran, and the
    /// error, which names the tables, is kept as its last error.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="supervisor">Runs the conversion.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the conversion runs to its end: only the shutdown of the daemon cancels it, and
    /// never while p4d switches the database.
    /// </param>
    /// <response code="202">The conversion has begun.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">
    /// The instance is not ready, another operation is in progress, or it already runs in Unicode mode.
    /// </response>
    [HttpPost("{id:guid}/convert-to-unicode")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public Task<ActionResult<InstanceDto>> ConvertInstanceToUnicode(
        Guid id,
        [FromServices] InstanceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        return RunOperationAsync(() => supervisor.ConvertInstanceToUnicodeAsync(id, cancellationToken));
    }

    /// <summary>
    /// Starts the p4d of several instances in parallel, and waits until each one accepts connections or fails. An
    /// instance that already runs is skipped.
    /// </summary>
    /// <param name="request">
    /// The instances, or all of them: every ready instance. Checked by <see cref="InstanceSelectionRequestValidator"/>.
    /// </param>
    /// <param name="batchRunner">Runs the starts.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the starts run to their end: only the shutdown of the daemon cancels them.
    /// </param>
    /// <response code="200">One result per instance, by name: started, skipped, or failed with its error.</response>
    /// <response code="400">Neither a list of instances nor all of them is selected, or both are.</response>
    /// <response code="404">An instance of the list does not exist. Nothing is started.</response>
    [HttpPost("start")]
    [ProducesResponseType<IReadOnlyList<InstanceActionResult>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public Task<ActionResult<IReadOnlyList<InstanceActionResult>>> StartInstances(
        InstanceSelectionRequest request,
        [FromServices] InstanceBatchRunner batchRunner,
        CancellationToken cancellationToken)
    {
        return RunBatchAsync(batchRunner, InstanceAction.Start, request, cancellationToken);
    }

    /// <summary>
    /// Stops the p4d of several instances in parallel, and waits until each one exits or fails. An instance that is
    /// already stopped is skipped.
    /// </summary>
    /// <param name="request">
    /// The instances, or all of them: every ready instance. Checked by <see cref="InstanceSelectionRequestValidator"/>.
    /// </param>
    /// <param name="batchRunner">Runs the stops.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the stops run to their end: only the shutdown of the daemon cancels them.
    /// </param>
    /// <response code="200">One result per instance, by name: stopped, skipped, or failed with its error.</response>
    /// <response code="400">Neither a list of instances nor all of them is selected, or both are.</response>
    /// <response code="404">An instance of the list does not exist. Nothing is stopped.</response>
    [HttpPost("stop")]
    [ProducesResponseType<IReadOnlyList<InstanceActionResult>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public Task<ActionResult<IReadOnlyList<InstanceActionResult>>> StopInstances(
        InstanceSelectionRequest request,
        [FromServices] InstanceBatchRunner batchRunner,
        CancellationToken cancellationToken)
    {
        return RunBatchAsync(batchRunner, InstanceAction.Stop, request, cancellationToken);
    }

    /// <summary>
    /// Restarts the p4d of several instances in parallel, and waits until each one accepts connections again or
    /// fails. A listed instance that is stopped is started. With all the instances, the stopped ones are skipped
    /// instead: only the running servers are restarted.
    /// </summary>
    /// <param name="request">
    /// The instances, or all of them: every ready instance. Checked by <see cref="InstanceSelectionRequestValidator"/>.
    /// </param>
    /// <param name="batchRunner">Runs the restarts.</param>
    /// <param name="cancellationToken">
    /// Aborts the request. Once begun, the restarts run to their end: only the shutdown of the daemon cancels them.
    /// </param>
    /// <response code="200">One result per instance, by name: restarted, skipped, or failed with its error.</response>
    /// <response code="400">Neither a list of instances nor all of them is selected, or both are.</response>
    /// <response code="404">An instance of the list does not exist. Nothing is restarted.</response>
    [HttpPost("restart")]
    [ProducesResponseType<IReadOnlyList<InstanceActionResult>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public Task<ActionResult<IReadOnlyList<InstanceActionResult>>> RestartInstances(
        InstanceSelectionRequest request,
        [FromServices] InstanceBatchRunner batchRunner,
        CancellationToken cancellationToken)
    {
        return RunBatchAsync(batchRunner, InstanceAction.Restart, request, cancellationToken);
    }

    /// <summary>
    /// Deletes an instance: stops its p4d if it runs, then removes it from the daemon. The instance is
    /// <c>Deleting</c> meanwhile, and the <c>InstanceDeleted</c> event of the <c>/hubs/instances</c> hub tells the
    /// clients once it is gone. A failed instance can be deleted too.
    /// </summary>
    /// <param name="id">Identifier of the instance.</param>
    /// <param name="deleteFiles">
    /// Also deletes the files of the instance in the data directory: its logs and, when the daemon chose it, its server
    /// directory with the root, the journals and the depots. A server directory given at creation is always kept.
    /// False by default: the files stay on disk.
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

    private async Task<ActionResult<IReadOnlyList<InstanceActionResult>>> RunBatchAsync(
        InstanceBatchRunner batchRunner,
        InstanceAction action,
        InstanceSelectionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await batchRunner.RunAsync(action, request, cancellationToken));
        }
        catch (KeyNotFoundException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status404NotFound);
        }
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
        return instance.ToDto(runtimeRegistry.Get(instance.Id));
    }
}
