using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace P4ServerManager.Daemon.Hosting;

/// <summary>
/// Authenticates the requests that come through the socket of the CLI. The socket is only open to the user and the
/// group of the daemon: whoever can open it is trusted, as before the web interface had accounts.
/// </summary>
public sealed class CliSocketAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CliSocket";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!CliSocket.IsCliSocketRequest(Context))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, "cli")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
