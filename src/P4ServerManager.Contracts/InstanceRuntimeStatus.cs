namespace P4ServerManager.Contracts;

/// <summary>
/// Whether the p4d process of an instance runs, or is being started, stopped, restarted, upgraded, converted to Unicode
/// mode, checked or deleted by the daemon.
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

    /// <summary>
    /// Moved to another p4d build: stopped, its database saved and upgraded, then started again if it ran.
    /// </summary>
    Upgrading,

    /// <summary>
    /// Moved to Unicode mode: stopped, its database saved and switched, then started again if it ran.
    /// </summary>
    ConvertingToUnicode,

    /// <summary>
    /// Its database is being checked: stopped, checked, then started again if it ran.
    /// </summary>
    CheckingHealth,
}

public static class InstanceRuntimeStatusExtensions
{
    /// <summary>
    /// Whether a start, a stop, a restart, an upgrade, a conversion to Unicode mode, a health check or a deletion is in
    /// progress.
    /// </summary>
    public static bool IsTransitional(this InstanceRuntimeStatus status)
    {
        return status is InstanceRuntimeStatus.Starting
            or InstanceRuntimeStatus.Stopping
            or InstanceRuntimeStatus.Restarting
            or InstanceRuntimeStatus.Deleting
            or InstanceRuntimeStatus.Upgrading
            or InstanceRuntimeStatus.ConvertingToUnicode
            or InstanceRuntimeStatus.CheckingHealth;
    }
}
