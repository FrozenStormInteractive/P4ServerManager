using Microsoft.AspNetCore.SignalR;

using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Services.Operations;

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

    Task OperationProgress(OperationSnapshot snapshot);
}
