using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Hosting;
using P4ServerManager.Daemon.Services.Authentication;

using UserEntity = P4ServerManager.Daemon.Database.Entities.User;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The accounts that sign in to the web interface. Every account has all rights on the servers, but only the
/// administrators manage the accounts: the other users get 403 here, and change their own password with
/// <c>PUT /api/auth/password</c>. Passwords are accepted but never returned. Through the socket of the CLI, these
/// endpoints need no account: that is how a lost password is reset.
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize(Policy = DaemonAuthentication.AdministratorPolicy)]
[Produces("application/json")]
[Tags("Users")]
public sealed class UsersController(UserService users) : ControllerBase
{
    /// <summary>
    /// Lists the accounts.
    /// </summary>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The accounts, by name.</response>
    /// <response code="401">No user is signed in.</response>
    /// <response code="403">The user is not an administrator.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<UserDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> GetUsers(CancellationToken cancellationToken)
    {
        return (await users.ListAsync(cancellationToken)).Select(u => u.ToDto()).ToList();
    }

    /// <summary>
    /// Gets an account.
    /// </summary>
    /// <param name="id">Identifier of the account.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The account.</response>
    /// <response code="401">No user is signed in.</response>
    /// <response code="403">The user is not an administrator.</response>
    /// <response code="404">No account has this identifier.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<UserDto>> GetUser(Guid id, CancellationToken cancellationToken)
    {
        UserEntity? user = await users.FindAsync(id, cancellationToken);
        return user is null ? Problem(statusCode: StatusCodes.Status404NotFound) : user.ToDto();
    }

    /// <summary>
    /// Adds an account.
    /// </summary>
    /// <param name="request">The account, checked by <see cref="CreateUserRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="201">The account is added.</response>
    /// <response code="400">The account is not valid, or another one has this name.</response>
    /// <response code="401">No user is signed in.</response>
    /// <response code="403">The user is not an administrator.</response>
    /// <response code="409">Another account got this name in the meantime.</response>
    [HttpPost]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<UserDto>> CreateUser(CreateUserRequest request, CancellationToken cancellationToken)
    {
        try
        {
            UserEntity user = await users.CreateAsync(
                request.UserName,
                request.Password,
                request.IsAdministrator,
                firstOnly: false,
                cancellationToken);
            return CreatedAtAction(nameof(GetUser), new { id = user.Id }, user.ToDto());
        }
        catch (UserConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Sets a new password on an account, without its current one, such as when its user forgot it. The sessions of
    /// the account end, the current one included when it is the account of the caller.
    /// </summary>
    /// <param name="id">Identifier of the account.</param>
    /// <param name="request">The password, checked by <see cref="SetUserPasswordRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="204">The password is set.</response>
    /// <response code="400">The password is not valid.</response>
    /// <response code="401">No user is signed in.</response>
    /// <response code="403">The user is not an administrator.</response>
    /// <response code="404">No account has this identifier.</response>
    [HttpPut("{id:guid}/password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult> SetPassword(
        Guid id,
        SetUserPasswordRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await users.SetPasswordAsync(id, request.Password, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Names an account administrator, or takes this right back. The sessions of the account go on, with the new
    /// right. The last administrator cannot lose it.
    /// </summary>
    /// <param name="id">Identifier of the account.</param>
    /// <param name="request">The right, checked by <see cref="SetUserAdministratorRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The account, with its new right.</response>
    /// <response code="401">No user is signed in.</response>
    /// <response code="403">The user is not an administrator.</response>
    /// <response code="404">No account has this identifier.</response>
    /// <response code="409">The account is the last administrator.</response>
    [HttpPut("{id:guid}/administrator")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<UserDto>> SetAdministrator(
        Guid id,
        SetUserAdministratorRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await users.SetAdministratorAsync(id, request.IsAdministrator, cancellationToken)).ToDto();
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        catch (UserConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Deletes an account. Its sessions end. The last administrator cannot be deleted.
    /// </summary>
    /// <param name="id">Identifier of the account.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="204">The account is deleted.</response>
    /// <response code="401">No user is signed in.</response>
    /// <response code="403">The user is not an administrator.</response>
    /// <response code="404">No account has this identifier.</response>
    /// <response code="409">The account is the last administrator.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult> DeleteUser(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await users.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound);
        }
        catch (UserConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
