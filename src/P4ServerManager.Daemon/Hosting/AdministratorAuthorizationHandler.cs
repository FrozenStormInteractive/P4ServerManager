using Microsoft.AspNetCore.Authorization;

using P4ServerManager.Daemon.Services.Authentication;

namespace P4ServerManager.Daemon.Hosting;

/// <summary>
/// Checks <see cref="AdministratorRequirement"/>. The right is read from the database on each request rather than kept
/// in the session cookie, so that it is taken back at once, without ending the sessions of the account.
/// </summary>
public sealed class AdministratorAuthorizationHandler(UserService users)
    : AuthorizationHandler<AdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdministratorRequirement requirement)
    {
        if (context.User.Identities.Any(i => i.AuthenticationType == CliSocketAuthenticationHandler.SchemeName))
        {
            context.Succeed(requirement);
            return;
        }

        CancellationToken cancellationToken = context.Resource is HttpContext httpContext
            ? httpContext.RequestAborted
            : CancellationToken.None;
        if (context.User.GetUserId() is { } id && await users.IsAdministratorAsync(id, cancellationToken))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Asks for an administrator: the CLI through the socket, or a user whose account is an administrator.
/// </summary>
public sealed class AdministratorRequirement : IAuthorizationRequirement;
