using System.Security.Claims;

using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Services.Authentication;

/// <summary>
/// The claims of the session of a user of the web interface, kept in its cookie.
/// </summary>
public static class UserClaims
{
    /// <summary>
    /// The security stamp of the account when the user signed in.
    /// </summary>
    public const string SecurityStamp = "p4sm:stamp";

    public static ClaimsPrincipal CreatePrincipal(User user, string authenticationScheme)
    {
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.UserName),
                new Claim(SecurityStamp, user.SecurityStamp.ToString()),
            ],
            authenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// The identifier of the account of a signed-in user. Null for the CLI, which reaches the API through the socket.
    /// </summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        return Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;
    }

    public static Guid? GetSecurityStamp(this ClaimsPrincipal principal)
    {
        return Guid.TryParse(principal.FindFirstValue(SecurityStamp), out Guid stamp) ? stamp : null;
    }
}
