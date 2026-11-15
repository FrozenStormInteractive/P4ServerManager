using P4ServerManager.Daemon.Options;
using P4ServerManager.Daemon.Services;

namespace P4ServerManager.Daemon.Hosting;

/// <summary>
/// The Unix domain socket the CLI reaches the API through, next to the TCP port of the web UI. Its requests need no
/// sign-in (<see cref="CliSocketAuthenticationHandler"/>): the socket is only open to the user and the group of the
/// daemon, and whoever can open it can manage the servers and the accounts of the web interface.
/// </summary>
public static class CliSocket
{
    // What Kestrel listens on when no URL is configured.
    private const string DefaultUrl = "http://localhost:5000";

    /// <summary>
    /// Makes Kestrel listen on the socket too, and keeps its file.
    /// </summary>
    public static WebApplicationBuilder AddCliSocket(this WebApplicationBuilder builder)
    {
        DaemonOptions options = builder.Configuration.GetSection(DaemonOptions.SectionName).Get<DaemonOptions>()
            ?? new DaemonOptions();
        string? socketPath = new DaemonPaths(Microsoft.Extensions.Options.Options.Create(options), builder.Environment)
            .SocketPath;
        if (socketPath is null)
        {
            return builder;
        }

        // An address set in code, such as with ListenUnixSocket, would make Kestrel ignore ASPNETCORE_URLS and
        // --urls: the socket is added to the URLs instead.
        string[] urls = (builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey) ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        builder.WebHost.UseUrls([.. urls.Length == 0 ? [DefaultUrl] : urls, GetUrl(socketPath)]);

        builder.Services.AddHostedService<CliSocketFile>();
        return builder;
    }

    /// <summary>
    /// The URL Kestrel listens on the socket at. Kestrel only reads a socket path after <c>unix:/</c>: on Windows, the
    /// path follows that slash, with forward slashes.
    /// </summary>
    public static string GetUrl(string socketPath)
    {
        return OperatingSystem.IsWindows()
            ? $"http://unix:/{socketPath.Replace('\\', '/')}"
            : $"http://unix:{socketPath}";
    }

    /// <summary>
    /// Whether the request came through the socket of the CLI.
    /// </summary>
    public static bool IsCliSocketRequest(HttpContext context)
    {
        // Kestrel gives an IP address to the connections of its TCP endpoints only.
        return context.Connection.LocalIpAddress is null;
    }
}
