using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using P4ServerManager.Daemon.Hosting;
using P4ServerManager.Daemon.Services;

namespace P4ServerManager.Daemon.Tests.Hosting;

public sealed class CliSocketTests : IAsyncLifetime
{
    private const UnixFileMode SocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));

    private string SocketPath => new DaemonPaths(dataDirectory).SocketPath!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(dataDirectory);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Daemon_AnswersThroughTheSocket_AfterRemovingAStaleFile()
    {
        // A daemon killed without stopping leaves its socket file behind.
        await File.WriteAllTextAsync(SocketPath, string.Empty, TestContext.Current.CancellationToken);

        // The real daemon, in its own process: WebApplicationFactory cannot read the address of a socket on Windows.
        using DaemonProcess daemon = DaemonProcess.Start(dataDirectory);
        using HttpClient client = CreateSocketClient(SocketPath);
        HttpResponseMessage response = await daemon.WaitForSocketAsync(
            () => client.GetAsync("api/instances", TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        JsonElement instances = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        instances.GetArrayLength().ShouldBe(0);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(SocketPath).ShouldBe(SocketMode);
        }
    }

    [Fact]
    public async Task Api_AsksTheTcpPortForASession_ButNotTheSocket()
    {
        using DaemonProcess daemon = DaemonProcess.Start(dataDirectory);
        using HttpClient socketClient = CreateSocketClient(SocketPath);
        HttpResponseMessage socketResponse = await daemon.WaitForSocketAsync(
            () => socketClient.GetAsync("api/users", TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        using HttpClient tcpClient = new() { BaseAddress = daemon.GetTcpAddress() };

        HttpResponseMessage tcpResponse = await tcpClient.GetAsync("api/users", TestContext.Current.CancellationToken);
        HttpResponseMessage sessionResponse = await tcpClient.GetAsync(
            "api/auth/session",
            TestContext.Current.CancellationToken);

        socketResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        tcpResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        sessionResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Starting_FailsWhenAnotherProcessListensOnTheSocket()
    {
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen();
        CliSocketFile socketFile = new(new DaemonPaths(dataDirectory), NullLogger<CliSocketFile>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(
            () => socketFile.StartingAsync(TestContext.Current.CancellationToken));

        File.Exists(SocketPath).ShouldBeTrue();
    }

    private static HttpClient CreateSocketClient(string socketPath)
    {
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
    }
}
