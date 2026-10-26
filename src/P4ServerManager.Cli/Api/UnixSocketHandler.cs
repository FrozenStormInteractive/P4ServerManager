using System.Net.Sockets;

namespace P4ServerManager.Cli.Api;

/// <summary>
/// Sends the HTTP requests of the CLI through the Unix domain socket of the daemon.
/// </summary>
internal static class UnixSocketHandler
{
    public static HttpMessageHandler Create(string socketPath)
    {
        return new SocketsHttpHandler
        {
            // A redirection would lead off the socket, to a TCP endpoint of the daemon.
            AllowAutoRedirect = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }
}
