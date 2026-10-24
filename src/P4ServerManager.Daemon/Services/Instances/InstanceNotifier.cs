using Microsoft.AspNetCore.SignalR;

using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Hubs;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Pushes the changes of the instances to the SignalR clients. A failed push is logged and never breaks the operation
/// that triggered it.
/// </summary>
public sealed class InstanceNotifier(
    IHubContext<InstancesHub, IInstancesClient> hubContext,
    InstanceRuntimeRegistry runtimeRegistry,
    ILogger<InstanceNotifier> logger)
{
    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    public async Task InstanceChangedAsync(P4ServerInstance instance)
    {
        try
        {
            InstanceDto dto = InstanceDto.From(instance, runtimeRegistry.Get(instance.Id));
            await hubContext.Clients.All.InstanceUpdated(dto);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not push the update of instance {Name}", instance.Name);
        }
    }

    public async Task InstanceDeletedAsync(Guid instanceId)
    {
        try
        {
            await hubContext.Clients.All.InstanceDeleted(instanceId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not push the deletion of instance {Id}", instanceId);
        }
    }

    public async Task OperationChangedAsync(OperationSnapshot snapshot)
    {
        try
        {
            await hubContext.Clients.All.OperationProgress(snapshot);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not push the operation on instance {Id}", snapshot.InstanceId);
        }
    }
}
