namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Whether the p4d process of an instance runs.
/// </summary>
public enum InstanceRuntimeStatus
{
    /// <summary>
    /// Not known yet, for example while the instance is being provisioned.
    /// </summary>
    Unknown,
    Running,
    Stopped,
}
