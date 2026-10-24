namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Whether the p4d process of an instance runs, or is being started, stopped, restarted or deleted by the daemon.
/// </summary>
public enum InstanceRuntimeStatus
{
    /// <summary>
    /// Not known yet, for example while the instance is being provisioned.
    /// </summary>
    Unknown,
    Running,
    Stopped,
    Starting,
    Stopping,

    /// <summary>
    /// Stopped then started again, as one operation.
    /// </summary>
    Restarting,

    /// <summary>
    /// Stopped then removed, with its files if asked for, as one operation.
    /// </summary>
    Deleting,
}

public static class InstanceRuntimeStatusExtensions
{
    /// <summary>
    /// Whether a start, a stop, a restart or a deletion is in progress.
    /// </summary>
    public static bool IsTransitional(this InstanceRuntimeStatus status)
    {
        return status is InstanceRuntimeStatus.Starting
            or InstanceRuntimeStatus.Stopping
            or InstanceRuntimeStatus.Restarting
            or InstanceRuntimeStatus.Deleting;
    }
}
