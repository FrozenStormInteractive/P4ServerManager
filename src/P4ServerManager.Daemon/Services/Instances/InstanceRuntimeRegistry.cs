using System.Collections.Concurrent;

using P4ServerManager.Contracts;

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

    /// <summary>
    /// Raised when the p4d of an instance is found running and was not before: started, restarted, or adopted. The
    /// handlers run on the caller of <see cref="Set"/> and must return at once.
    /// </summary>
    public event Action<Guid>? Started;

    /// <returns>True when the status changed.</returns>
    public bool Set(Guid instanceId, InstanceRuntimeStatus status)
    {
        InstanceRuntimeStatus previous = statuses.GetValueOrDefault(instanceId, InstanceRuntimeStatus.Unknown);
        statuses[instanceId] = status;
        if (status == InstanceRuntimeStatus.Running && previous != InstanceRuntimeStatus.Running)
        {
            Started?.Invoke(instanceId);
        }
        return previous != status;
    }

    public void Remove(Guid instanceId)
    {
        statuses.TryRemove(instanceId, out _);
    }
}
