namespace P4ServerManager.Contracts;

/// <summary>
/// Whether the p4d process of an instance runs, or is being started, stopped, restarted, upgraded, converted to Unicode
/// mode, checked, given another server ID or port, or deleted by the daemon.
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

    /// <summary>
    /// Given another server ID: stopped if it ran, its server ID set, started to rename its server spec, then stopped
    /// again if it did not run.
    /// </summary>
    ChangingServerId,

    /// <summary>
    /// Moved to another port: stopped if it ran, its port changed, started on it to update the address of its server
    /// spec when a super user is stored, then stopped again if it did not run.
    /// </summary>
    ChangingPort,

    /// <summary>
    /// Backed up while it keeps running: a checkpoint or a rotation of its journal, then its files uploaded to its
    /// backup destinations.
    /// </summary>
    BackingUp,

    /// <summary>
    /// A backup point of it is being checked at a destination. p4d is not touched.
    /// </summary>
    VerifyingBackup,

    /// <summary>
    /// A backup point replaces its data: p4d is stopped while the files are restored, then started to verify them.
    /// </summary>
    Restoring,
}

public static class InstanceRuntimeStatusExtensions
{
    /// <summary>
    /// Whether a start, a stop, a restart, an upgrade, a conversion to Unicode mode, a health check, a change of the
    /// server ID or of the port, or a deletion is in progress.
    /// </summary>
    public static bool IsTransitional(this InstanceRuntimeStatus status)
    {
        return status is InstanceRuntimeStatus.Starting
            or InstanceRuntimeStatus.Stopping
            or InstanceRuntimeStatus.Restarting
            or InstanceRuntimeStatus.Deleting
            or InstanceRuntimeStatus.Upgrading
            or InstanceRuntimeStatus.ConvertingToUnicode
            or InstanceRuntimeStatus.CheckingHealth
            or InstanceRuntimeStatus.ChangingServerId
            or InstanceRuntimeStatus.ChangingPort
            or InstanceRuntimeStatus.BackingUp
            or InstanceRuntimeStatus.VerifyingBackup
            or InstanceRuntimeStatus.Restoring;
    }
}
