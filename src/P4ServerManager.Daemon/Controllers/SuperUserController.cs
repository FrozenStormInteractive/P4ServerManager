using Microsoft.AspNetCore.Mvc;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Admin;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The super user account the daemon stores for an instance, to administer its server. Its password is stored
/// encrypted and never returned. The daemon checks the account at startup, when the server starts, and periodically:
/// the result is the <c>superUser</c> of the instance.
/// </summary>
[ApiController]
[Route("api/instances/{instanceId:guid}/super-user")]
[Produces("application/json")]
[Tags("Instances")]
public sealed class SuperUserController(
    SuperUserService superUsers,
    InstanceRuntimeRegistry runtimeRegistry) : ControllerBase
{
    /// <summary>
    /// Stores the super user of an instance, in place of the current one: another account, or the new password of the
    /// same account after it was changed in P4. Nothing changes in P4. When the server runs, the account must log in
    /// and have super access, or it is refused. Otherwise it is stored <c>Unchecked</c>, and checked once the server
    /// runs.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="request">The account, checked by <see cref="SetSuperUserRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The instance with its new super user.</response>
    /// <response code="400">
    /// The account is not valid, or the server refused it: <c>password</c> for a wrong or expired password,
    /// <c>userName</c> for an account without super access. The errors are keyed by field.
    /// </response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">The instance is not ready, or another operation is in progress on it.</response>
    [HttpPut]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<InstanceDto>> SetSuperUser(
        Guid instanceId,
        SetSuperUserRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            P4ServerInstance instance = await superUsers.SetAsync(
                instanceId,
                new SuperUserCredentials(request.UserName, request.Password),
                cancellationToken);
            return ToDto(instance);
        }
        catch (SuperUserRejectedException exception)
        {
            string field = exception.Status == SuperUserStatus.InsufficientAccess ? "userName" : "password";
            ModelState.AddModelError(field, exception.Message);
            return ValidationProblem(ModelState);
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
    /// Checks the stored super user of an instance now, even one already found invalid: the daemon does not check
    /// such an account again on its own, since p4d holds failed logins against it.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The instance, with the result of the check in its <c>superUser</c>.</response>
    /// <response code="404">No instance has this identifier.</response>
    /// <response code="409">No super user is stored, or the server does not run.</response>
    /// <response code="500">The server could not be reached.</response>
    [HttpPost("check")]
    [ProducesResponseType<InstanceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<ActionResult<InstanceDto>> CheckSuperUser(Guid instanceId, CancellationToken cancellationToken)
    {
        try
        {
            return ToDto(await superUsers.CheckAsync(instanceId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        catch (SuperUserUnavailableException exception)
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
