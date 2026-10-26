using System.Net.Sockets;
using System.Text;

using P4ServerManager.Cli.Api;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Api;

public sealed class UnixSocketHandlerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "p4sm-cli-tests", Guid.NewGuid().ToString("N"));

    public UnixSocketHandlerTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string SocketPath => Path.Combine(_directory, "daemon.sock");

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Requests_GoThroughTheSocket()
    {
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen();
        Task<string> server = AnswerOnceAsync(listener, "[]");

        using DaemonClient client = CreateClient();
        IReadOnlyList<InstanceDto> instances = await client.GetInstancesAsync(TestContext.Current.CancellationToken);

        instances.ShouldBeEmpty();
        (await server).ShouldStartWith("GET /api/instances HTTP/1.1");
    }

    [Fact]
    public async Task Requests_FailClearlyWhenNothingListens()
    {
        using DaemonClient client = CreateClient();

        CliException exception = await Should.ThrowAsync<CliException>(
            () => client.GetInstancesAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldBe(
            $"Cannot reach the daemon at {SocketPath}. Is it running? Set --socket or P4SM_SOCKET.");
    }

    private DaemonClient CreateClient()
    {
        HttpClient httpClient = new(UnixSocketHandler.Create(SocketPath)) { BaseAddress = new Uri("http://localhost/") };
        return new DaemonClient(httpClient, SocketPath);
    }

    // Reads one request and answers it with a JSON body. Returns the request.
    private static async Task<string> AnswerOnceAsync(Socket listener, string json)
    {
        using Socket connection = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        StringBuilder request = new();
        byte[] buffer = new byte[4096];
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await connection.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        string response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}";
        await connection.SendAsync(Encoding.UTF8.GetBytes(response), TestContext.Current.CancellationToken);
        return request.ToString();
    }
}
