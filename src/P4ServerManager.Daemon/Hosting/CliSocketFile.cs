using System.Net.Sockets;

using P4ServerManager.Daemon.Services;

namespace P4ServerManager.Daemon.Hosting;

/// <summary>
/// Removes the socket file a crashed daemon left behind before Kestrel binds it, then restricts its permissions.
/// </summary>
internal sealed class CliSocketFile(DaemonPaths paths, ILogger<CliSocketFile> logger) : IHostedLifecycleService
{
    private const UnixFileMode SocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    // Runs before the hosted services start, so before Kestrel binds the socket.
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (paths.SocketPath is not { } socketPath)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        if (!File.Exists(socketPath))
        {
            return;
        }

        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        }
        catch (SocketException)
        {
            // Nothing listens: the file is left from a daemon that did not stop cleanly.
            logger.LogInformation("Removing the socket {SocketPath} left by a previous run", socketPath);
            File.Delete(socketPath);
            return;
        }
        throw new InvalidOperationException($"Another process listens on {socketPath}. Is another daemon running?");
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (paths.SocketPath is { } socketPath && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(socketPath, SocketMode);
        }
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
