using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Hosting;
using P4ServerManager.Daemon.Services.Authentication;

using UserEntity = P4ServerManager.Daemon.Database.Entities.User;

namespace P4ServerManager.Daemon.Controllers;

/// <summary>
/// The session of a user of the web interface. A user signs in with an account and gets a session cookie, which every
/// other request through the TCP port needs. Requests that change something also carry the <c>X-P4SM-Request</c>
/// header. The CLI needs neither: it reaches the API through the socket of the daemon. While no account exists, the
/// first one is created with <c>POST /api/auth/setup</c>.
/// </summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
[Tags("Authentication")]
public sealed class AuthController(UserService users, ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>
    /// Tells whether a user is signed in, and whether the daemon still waits for its first account. Needs no session.
    /// </summary>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The session.</response>
    [HttpGet("session")]
    [AllowAnonymous]
    [ProducesResponseType<AuthSessionDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthSessionDto>> GetSession(CancellationToken cancellationToken)
    {
        bool setupRequired = !await users.AnyAsync(cancellationToken);
        UserEntity? user = User.GetUserId() is { } id ? await users.FindAsync(id, cancellationToken) : null;
        return new AuthSessionDto(setupRequired, user?.ToDto());
    }

    /// <summary>
    /// Creates the first account, and signs it in. Only allowed while no account exists: then, anybody who reaches
    /// the daemon can do it, so create it right after the installation.
    /// </summary>
    /// <param name="request">The account, checked by <see cref="SetupRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="201">The account is created, and its session cookie set.</response>
    /// <response code="400">The account is not valid.</response>
    /// <response code="409">An account exists already.</response>
    /// <response code="429">Too many attempts from this address in the last minute.</response>
    [HttpPost("setup")]
    [AllowAnonymous]
    [EnableRateLimiting(DaemonAuthentication.SignInRateLimitPolicy)]
    [ProducesResponseType<AuthSessionDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<ActionResult<AuthSessionDto>> Setup(SetupRequest request, CancellationToken cancellationToken)
    {
        try
        {
            UserEntity user = await users.CreateAsync(
                request.UserName,
                request.Password,
                firstOnly: true,
                cancellationToken);
            user = await users.RecordSignInAsync(user, cancellationToken);
            await SignInAsync(user, persistent: false);
            logger.LogInformation("Created the first account {User} from {Address}", user.UserName, RemoteAddress);
            return CreatedAtAction(nameof(GetSession), new AuthSessionDto(false, user.ToDto()));
        }
        catch (UserConflictException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Signs a user in, and sets its session cookie. The session lasts while the user makes requests, up to the
    /// configured lifetime between two requests.
    /// </summary>
    /// <param name="request">The account, checked by <see cref="LoginRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The user is signed in.</response>
    /// <response code="400">The user name or the password is missing.</response>
    /// <response code="401">No account has this user name and this password.</response>
    /// <response code="429">Too many attempts from this address in the last minute.</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(DaemonAuthentication.SignInRateLimitPolicy)]
    [ProducesResponseType<AuthSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<ActionResult<AuthSessionDto>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        UserEntity? user = await users.VerifyAsync(request.UserName, request.Password, cancellationToken);
        if (user is null)
        {
            logger.LogWarning("Failed sign-in of {User} from {Address}", request.UserName, RemoteAddress);
            return Problem("The user name or the password is not correct.", statusCode: StatusCodes.Status401Unauthorized);
        }

        await SignInAsync(user, request.RememberMe);
        logger.LogInformation("Signed in {User} from {Address}", user.UserName, RemoteAddress);
        return new AuthSessionDto(false, user.ToDto());
    }

    /// <summary>
    /// Signs the user out: its session cookie is removed.
    /// </summary>
    /// <response code="204">The user is signed out.</response>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    /// <summary>
    /// Changes the password of the signed-in user. Its other sessions end; this one goes on. Not for the CLI, which
    /// sets a password with <c>PUT /api/users/{id}/password</c>.
    /// </summary>
    /// <param name="request">The passwords, checked by <see cref="ChangePasswordRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="204">The password is changed.</response>
    /// <response code="400">
    /// The new password is not valid, or <c>currentPassword</c> is not the right one. The errors are keyed by field.
    /// </response>
    /// <response code="401">No user is signed in.</response>
    [HttpPut("password")]
    [Authorize(Policy = DaemonAuthentication.WebUserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> ChangePassword(
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            UserEntity user = await users.ChangePasswordAsync(
                User.GetUserId()!.Value,
                request.CurrentPassword,
                request.NewPassword,
                cancellationToken);
            // The cookie carries the new security stamp, so that this session goes on.
            AuthenticateResult current = await HttpContext.AuthenticateAsync(
                CookieAuthenticationDefaults.AuthenticationScheme);
            await SignInAsync(user, current.Properties?.IsPersistent ?? false);
            return NoContent();
        }
        catch (UserPasswordMismatchException exception)
        {
            ModelState.AddModelError("currentPassword", exception.Message);
            return ValidationProblem(ModelState);
        }
        catch (KeyNotFoundException)
        {
            return Unauthorized();
        }
    }

    private string? RemoteAddress => HttpContext.Connection.RemoteIpAddress?.ToString();

    private Task SignInAsync(UserEntity user, bool persistent)
    {
        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserClaims.CreatePrincipal(user, CookieAuthenticationDefaults.AuthenticationScheme),
            new AuthenticationProperties { IsPersistent = persistent });
    }
}
