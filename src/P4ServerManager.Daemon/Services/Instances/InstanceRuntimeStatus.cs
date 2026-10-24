namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Whether the p4d process of an instance runs, or is being started, stopped or restarted by the daemon.
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
}

public static class InstanceRuntimeStatusExtensions
{
    /// <summary>
    /// Whether a start, a stop or a restart is in progress.
    /// </summary>
    public static bool IsTransitional(this InstanceRuntimeStatus status)
    {
        return status is InstanceRuntimeStatus.Starting
            or InstanceRuntimeStatus.Stopping
            or InstanceRuntimeStatus.Restarting;
    }
}
