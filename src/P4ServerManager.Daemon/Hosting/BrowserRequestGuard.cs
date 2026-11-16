namespace P4ServerManager.Daemon.Hosting;

/// <summary>
/// Keeps other web sites from using the session of a signed-in user. The cookie of the session is <c>SameSite=Strict</c>,
/// but the other services of the same machine, such as DSM on another port, are the same site. So, on the TCP port:
/// <list type="bullet">
/// <item>A request that changes something in the API carries the <see cref="HeaderName"/> header. A page of another
/// origin cannot send it without a CORS preflight, which the daemon never allows.</item>
/// <item>A connection to a hub that tells its origin comes from the origin of the daemon.</item>
/// </list>
/// The requests of the CLI, through its socket, are not concerned.
/// </summary>
public sealed class BrowserRequestGuard(RequestDelegate next)
{
    public const string HeaderName = "X-P4SM-Request";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!CliSocket.IsCliSocketRequest(context) && Rejects(context.Request) is { } detail)
        {
            await Results.Problem(detail, statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            return;
        }
        await next(context);
    }

    private static string? Rejects(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method)
            && !request.Headers.ContainsKey(HeaderName))
        {
            return $"The request lacks the {HeaderName} header.";
        }

        if (request.Path.StartsWithSegments("/hubs")
            && request.Headers.Origin.ToString() is { Length: > 0 } origin
            && !IsSameOrigin(origin, request))
        {
            return "The connection comes from another origin.";
        }

        return null;
    }

    // Compares the host and the port only: behind a reverse proxy that serves HTTPS, the daemon sees plain HTTP.
    private static bool IsSameOrigin(string origin, HttpRequest request)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
