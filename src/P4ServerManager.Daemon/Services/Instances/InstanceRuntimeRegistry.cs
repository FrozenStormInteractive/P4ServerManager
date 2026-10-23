using System.Collections.Concurrent;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Last known runtime status of each instance, in memory.
/// </summary>
public sealed class InstanceRuntimeRegistry
{
    private readonly ConcurrentDictionary<Guid, InstanceRuntimeStatus> statuses = new();

    public InstanceRuntimeStatus Get(Guid instanceId)
    {
        return statuses.GetValueOrDefault(instanceId, InstanceRuntimeStatus.Unknown);
    }

    /// <returns>True when the status changed.</returns>
    public bool Set(Guid instanceId, InstanceRuntimeStatus status)
    {
        InstanceRuntimeStatus previous = statuses.GetValueOrDefault(instanceId, InstanceRuntimeStatus.Unknown);
        statuses[instanceId] = status;
        return previous != status;
    }
}
