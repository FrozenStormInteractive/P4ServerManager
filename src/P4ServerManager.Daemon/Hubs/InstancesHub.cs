using Microsoft.AspNetCore.SignalR;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Hubs;

/// <summary>
/// Pushes the changes of the instances to the clients. Clients only listen.
/// </summary>
public sealed class InstancesHub : Hub<IInstancesClient>
{
    public const string Path = "/hubs/instances";
}

public interface IInstancesClient
{
    Task InstanceUpdated(InstanceDto instance);

    Task InstanceDeleted(Guid instanceId);

    Task OperationProgress(OperationSnapshot snapshot);

    Task HealthCheckUpdated(HealthCheckRunDto run);

    Task HealthCheckDeleted(Guid instanceId, Guid runId);
}
