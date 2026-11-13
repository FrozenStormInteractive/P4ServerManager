using System.Net.Http.Json;

using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Api;

// Backup points of the destinations, and their restores.
internal sealed partial class DaemonClient
{
    /// <summary>
    /// The backup points stored at a destination, newest first, read from the destination itself.
    /// </summary>
    public Task<IReadOnlyList<BackupPointDto>> GetBackupPointsAsync(
        Guid destinationId,
        Guid? sourceId,
        CancellationToken cancellationToken)
    {
        string query = sourceId is { } id ? $"?sourceId={id}" : string.Empty;
        return SendAsync(
            HttpMethod.Get,
            $"api/backup-destinations/{destinationId}/points{query}",
            Json.IReadOnlyListBackupPointDto,
            cancellationToken);
    }

    public Task<IReadOnlyList<RestoreRunDto>> GetRestoresAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Get,
            $"api/instances/{instanceId}/restores",
            Json.IReadOnlyListRestoreRunDto,
            cancellationToken);
    }

    public Task<RestoreRunDto> GetRestoreAsync(Guid instanceId, Guid id, CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Get,
            $"api/instances/{instanceId}/restores/{id}",
            Json.RestoreRunDto,
            cancellationToken);
    }

    /// <summary>
    /// Begins to replace the data of an instance with a backup point. It goes on in the daemon: follow it with
    /// <see cref="GetRestoreAsync"/>.
    /// </summary>
    public Task<RestoreRunDto> StartRestoreAsync(
        Guid instanceId,
        RestoreBackupRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Post,
            $"api/instances/{instanceId}/restores",
            Json.RestoreRunDto,
            cancellationToken,
            JsonContent.Create(request, Json.RestoreBackupRequest));
    }

    public async Task CancelRestoreAsync(Guid instanceId, Guid id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"api/instances/{instanceId}/restores/{id}/cancel"),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Makes a new instance from a backup point. Its provisioning goes on in the daemon.
    /// </summary>
    public Task<InstanceDto> RestoreInstanceAsync(RestoreInstanceRequest request, CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Post,
            "api/instances/restore",
            Json.InstanceDto,
            cancellationToken,
            JsonContent.Create(request, Json.RestoreInstanceRequest));
    }

    /// <summary>
    /// Finds the backup point a command names by the start of its identifier, newest first when several destinations
    /// hold it.
    /// </summary>
    public static BackupPointDto ResolveBackupPoint(IReadOnlyList<BackupPointDto> points, string reference)
    {
        List<BackupPointDto> matches = points
            .Where(p => p.Id.ToString().StartsWith(reference, StringComparison.OrdinalIgnoreCase)
                || p.Id.ToString("N").StartsWith(reference, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Select(p => p.Id).Distinct().Count() switch
        {
            1 => matches[0],
            0 => throw new CliException($"No backup point matches {reference}."),
            _ => throw new CliException($"Several backup points match {reference}. Give more of its identifier."),
        };
    }
}
