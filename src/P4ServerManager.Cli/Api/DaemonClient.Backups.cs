using System.Net.Http.Json;

using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Api;

// Backup destinations and backups of the instances.
internal sealed partial class DaemonClient
{
    public Task<IReadOnlyList<BackupDestinationDto>> GetBackupDestinationsAsync(CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Get,
            "api/backup-destinations",
            Json.IReadOnlyListBackupDestinationDto,
            cancellationToken);
    }

    public Task<BackupDestinationDto> CreateBackupDestinationAsync(
        CreateBackupDestinationRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Post,
            "api/backup-destinations",
            Json.BackupDestinationDto,
            cancellationToken,
            JsonContent.Create(request, Json.CreateBackupDestinationRequest));
    }

    public Task<BackupDestinationDto> UpdateBackupDestinationAsync(
        Guid id,
        UpdateBackupDestinationRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Put,
            $"api/backup-destinations/{id}",
            Json.BackupDestinationDto,
            cancellationToken,
            JsonContent.Create(request, Json.UpdateBackupDestinationRequest));
    }

    public async Task DeleteBackupDestinationAsync(Guid id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"api/backup-destinations/{id}"),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<BackupDestinationTestResult> TestBackupDestinationAsync(Guid id, CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Post,
            $"api/backup-destinations/{id}/test",
            Json.BackupDestinationTestResult,
            cancellationToken);
    }

    public Task<InstanceBackupDestinations> GetInstanceBackupDestinationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Get,
            $"api/instances/{instanceId}/backup-destinations",
            Json.InstanceBackupDestinations,
            cancellationToken);
    }

    public Task<InstanceBackupDestinations> SetInstanceBackupDestinationsAsync(
        Guid instanceId,
        InstanceBackupDestinations request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Put,
            $"api/instances/{instanceId}/backup-destinations",
            Json.InstanceBackupDestinations,
            cancellationToken,
            JsonContent.Create(request, Json.InstanceBackupDestinations));
    }

    public Task<IReadOnlyList<BackupRunDto>> GetBackupsAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Get,
            $"api/instances/{instanceId}/backups",
            Json.IReadOnlyListBackupRunDto,
            cancellationToken);
    }

    public Task<BackupRunDto> GetBackupAsync(Guid instanceId, Guid id, CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Get,
            $"api/instances/{instanceId}/backups/{id}",
            Json.BackupRunDto,
            cancellationToken);
    }

    /// <summary>
    /// Begins a backup of an instance. It goes on in the daemon: follow it with <see cref="GetBackupAsync"/>.
    /// </summary>
    public Task<BackupRunDto> StartBackupAsync(
        Guid instanceId,
        StartBackupRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Post,
            $"api/instances/{instanceId}/backups",
            Json.BackupRunDto,
            cancellationToken,
            JsonContent.Create(request, Json.StartBackupRequest));
    }

    public async Task CancelBackupAsync(Guid instanceId, Guid id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"api/instances/{instanceId}/backups/{id}/cancel"),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Begins the verification of a backup at one of its destinations. It goes on in the daemon.
    /// </summary>
    public Task<BackupRunDto> VerifyBackupAsync(
        Guid instanceId,
        Guid id,
        VerifyBackupRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Post,
            $"api/instances/{instanceId}/backups/{id}/verify",
            Json.BackupRunDto,
            cancellationToken,
            JsonContent.Create(request, Json.VerifyBackupRequest));
    }

    /// <summary>
    /// Finds the backup destination a command names, by its name or its identifier.
    /// </summary>
    public async Task<BackupDestinationDto> ResolveBackupDestinationAsync(
        string reference,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BackupDestinationDto> destinations = await GetBackupDestinationsAsync(cancellationToken);
        return ResolveBackupDestination(destinations, reference);
    }

    public static BackupDestinationDto ResolveBackupDestination(
        IReadOnlyList<BackupDestinationDto> destinations,
        string reference)
    {
        if (Guid.TryParse(reference, out Guid id) && destinations.FirstOrDefault(d => d.Id == id) is { } byId)
        {
            return byId;
        }
        List<BackupDestinationDto> byName = destinations
            .Where(d => string.Equals(d.Name, reference, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return byName.Count switch
        {
            1 => byName[0],
            0 => throw new CliException($"No backup destination is named {reference}."),
            _ => byName.FirstOrDefault(d => d.Name == reference)
                ?? throw new CliException($"Several backup destinations match {reference}. Type the name with its exact case."),
        };
    }
}
