using Microsoft.AspNetCore.Mvc;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Restore;
using P4ServerManager.Daemon.Services.Instances;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// Instances made from backup points: a copy of an instance, or an instance back after the loss of its machine, from
/// any destination this daemon knows, added again with its passphrase if needed.
/// </summary>
[ApiController]
[Route("api/instances/restore")]
[Produces("application/json")]
[Tags("Backups")]
public sealed class RestoredInstancesController(
    RestoredInstanceService restoredInstanceService,
    InstanceRuntimeRegistry runtimeRegistry) : ControllerBase
{
    /// <summary>
    /// Makes a new instance from a backup point. The point is read first: it must exist and fit the p4d build asked
    /// for, which cannot be older than the one that made it. The instance is created at once, in the Provisioning
    /// state, and the restore runs in the background as its provisioning: the operation of the instance tells its
    /// progress, and its restore is listed with the restores of the instance. Its case handling and Unicode mode come
    /// from the point; its server ID is its name; the depots kept outside <c>server.depot.root</c> go under it.
    /// </summary>
    /// <param name="request">The instance and the point, checked by <see cref="RestoreInstanceRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="202">The instance is created and its restore has begun.</response>
    /// <response code="400">The request is not valid. The errors are keyed by field.</response>
    /// <response code="404">The destination does not exist.</response>
    /// <response code="409">Another instance already uses this name or port.</response>
    /// <response code="422">The point cannot be read, or the p4d build asked for is older than the one of the point.</response>
    [HttpPost]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<ActionResult<InstanceDto>> RestoreInstance(
        RestoreInstanceRequest request,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance;
        try
        {
            instance = await restoredInstanceService.CreateAsync(request, cancellationToken);
        }
        catch (KeyNotFoundException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (RestoreSourceException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (InstanceConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }

        InstanceDto dto = await instance.ToDtoAsync(runtimeRegistry.Get(instance.Id), cancellationToken);
        return Accepted($"/api/instances/{instance.Id}", dto);
    }
}
