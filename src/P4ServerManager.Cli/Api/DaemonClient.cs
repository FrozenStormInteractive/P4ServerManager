using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using P4ServerManager.Cli.Json;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Api;

/// <summary>
/// Client of the REST API of the daemon.
/// </summary>
/// <param name="httpClient">Sends the requests to the daemon. Disposed with the client.</param>
/// <param name="socketPath">Socket the requests go through, named in the errors.</param>
internal sealed class DaemonClient(HttpClient httpClient, string socketPath) : IDisposable
{
    private static readonly CliJsonContext Json = CliJsonContext.Default;

    public Task<IReadOnlyList<InstanceDto>> GetInstancesAsync(CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Get, "api/instances", Json.IReadOnlyListInstanceDto, cancellationToken);
    }

    public Task<InstanceDto> GetInstanceAsync(Guid id, CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Get, $"api/instances/{id}", Json.InstanceDto, cancellationToken);
    }

    public Task<InstanceDto> CreateInstanceAsync(CreateInstanceRequest request, CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Post,
            "api/instances",
            Json.InstanceDto,
            cancellationToken,
            JsonContent.Create(request, Json.CreateInstanceRequest));
    }

    public Task<InstanceDto> UpdateInstanceAsync(
        Guid id,
        UpdateInstanceRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Patch,
            $"api/instances/{id}",
            Json.InstanceDto,
            cancellationToken,
            JsonContent.Create(request, Json.UpdateInstanceRequest));
    }

    public Task<InstanceDto> StartInstanceAsync(Guid id, CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Post, $"api/instances/{id}/start", Json.InstanceDto, cancellationToken);
    }

    public Task<InstanceDto> StopInstanceAsync(Guid id, CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Post, $"api/instances/{id}/stop", Json.InstanceDto, cancellationToken);
    }

    public Task<InstanceDto> RestartInstanceAsync(Guid id, CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Post, $"api/instances/{id}/restart", Json.InstanceDto, cancellationToken);
    }

    public async Task DeleteInstanceAsync(Guid id, bool deleteFiles, CancellationToken cancellationToken)
    {
        string uri = $"api/instances/{id}?deleteFiles={(deleteFiles ? "true" : "false")}";
        using HttpResponseMessage response = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, uri), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Gets the last long operation on an instance, or null when the daemon has run none since it started.
    /// </summary>
    public async Task<OperationSnapshot?> GetOperationAsync(Guid id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"api/instances/{id}/operation"),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadAsync(response, Json.OperationSnapshot, cancellationToken);
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string uri,
        JsonTypeInfo<T> responseType,
        CancellationToken cancellationToken,
        HttpContent? content = null)
    {
        using HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(method, uri) { Content = content },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadAsync(response, responseType, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            try
            {
                return await httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException exception) when (exception.InnerException is SocketException socketException)
            {
                throw socketException.SocketErrorCode == SocketError.AccessDenied
                    ? new CliException(
                        $"Permission denied on {socketPath}. Run as a user of the group of the daemon, or with sudo.",
                        exception)
                    : new CliException(
                        $"Cannot reach the daemon at {socketPath}. Is it running? Set --socket or "
                        + $"{GlobalOptions.SocketVariable}.",
                        exception);
            }
            catch (HttpRequestException exception)
            {
                throw new CliException($"The request to the daemon at {socketPath} failed: {exception.Message}", exception);
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiProblem? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync(Json.ApiProblem, cancellationToken);
        }
        catch (JsonException)
        {
            // Not a problem details document: the status code alone describes the error.
        }
        throw new DaemonApiException(response.StatusCode, problem);
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
    {
        return await response.Content.ReadFromJsonAsync(type, cancellationToken)
            ?? throw new CliException("The daemon answered with an empty body.");
    }
}
