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

    /// <summary>
    /// Begins the upgrade of an instance to another p4d release. It goes on in the daemon: follow it with
    /// <see cref="GetOperationAsync"/>.
    /// </summary>
    public Task<InstanceDto> UpgradeInstanceAsync(
        Guid id,
        UpgradeInstanceRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Post,
            $"api/instances/{id}/upgrade",
            Json.InstanceDto,
            cancellationToken,
            JsonContent.Create(request, Json.UpgradeInstanceRequest));
    }

    /// <summary>
    /// Begins the conversion of an instance to Unicode mode. It goes on in the daemon: follow it with
    /// <see cref="GetOperationAsync"/>.
    /// </summary>
    public Task<InstanceDto> ConvertInstanceToUnicodeAsync(Guid id, CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Post, $"api/instances/{id}/convert-to-unicode", Json.InstanceDto, cancellationToken);
    }

    /// <summary>
    /// Starts, stops or restarts several instances at once.
    /// </summary>
    /// <param name="action">The action: <c>start</c>, <c>stop</c> or <c>restart</c>.</param>
    /// <param name="selection">The instances, or all of them.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    public Task<IReadOnlyList<InstanceActionResult>> RunInstancesActionAsync(
        string action,
        InstanceSelectionRequest selection,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Post,
            $"api/instances/{action}",
            Json.IReadOnlyListInstanceActionResult,
            cancellationToken,
            JsonContent.Create(selection, Json.InstanceSelectionRequest));
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

    public Task<IReadOnlyList<BuildDto>> GetBuildsAsync(CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Get, "api/p4d/builds", Json.IReadOnlyListBuildDto, cancellationToken);
    }

    /// <summary>
    /// Imports a build from files on this machine: p4d and p4, or the server package of Perforce that holds them.
    /// </summary>
    /// <param name="files">The form fields and their files: <c>p4d</c> and <c>p4</c>, or <c>package</c>.</param>
    /// <param name="cancellationToken">Aborts the import.</param>
    public async Task<BuildDto> ImportBuildAsync(
        IReadOnlyList<(string Field, string Path)> files,
        CancellationToken cancellationToken)
    {
        using MultipartFormDataContent form = new();
        List<FileStream> streams = [];
        try
        {
            foreach ((string field, string path) in files)
            {
                FileStream stream = File.OpenRead(path);
                streams.Add(stream);
                form.Add(new StreamContent(stream), field, Path.GetFileName(path));
            }
            using HttpResponseMessage response = await SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "api/p4d/builds/import") { Content = form },
                cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return await ReadAsync(response, Json.BuildDto, cancellationToken);
        }
        finally
        {
            foreach (FileStream stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Queues the download of the current build of a release into the archive. It goes on in the daemon.
    /// </summary>
    public async Task DownloadBuildAsync(DownloadBuildRequest request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "api/p4d/builds/download")
            {
                Content = JsonContent.Create(request, Json.DownloadBuildRequest),
            },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<BuildDto> UpdateBuildAsync(Guid id, UpdateBuildRequest request, CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Patch,
            $"api/p4d/builds/{id}",
            Json.BuildDto,
            cancellationToken,
            JsonContent.Create(request, Json.UpdateBuildRequest));
    }

    public async Task DeleteBuildAsync(Guid id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"api/p4d/builds/{id}"),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<BuildArchivePolicyDto> GetArchivePolicyAsync(CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Get, "api/p4d/archive/policy", Json.BuildArchivePolicyDto, cancellationToken);
    }

    public Task<BuildArchiveStatusDto> GetArchiveStatusAsync(CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Get, "api/p4d/archive/status", Json.BuildArchiveStatusDto, cancellationToken);
    }

    /// <summary>
    /// Queues a check of the Perforce FTP for new builds. It goes on in the daemon.
    /// </summary>
    public async Task CheckArchiveAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "api/p4d/archive/check"),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
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
