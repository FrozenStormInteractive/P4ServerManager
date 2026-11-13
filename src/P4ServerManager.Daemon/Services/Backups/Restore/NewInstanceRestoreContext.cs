using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.PortChange;
using P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;
using P4ServerManager.Daemon.Services.Instances.Steps;

namespace P4ServerManager.Daemon.Services.Backups.Restore;

/// <summary>
/// State shared by the steps that make a new instance from a backup point. The instance is pinned to its build as soon
/// as it is resolved, as when it is created.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/> and <paramref name="run"/>.</param>
/// <param name="instance">The instance, in the Provisioning state.</param>
/// <param name="run">The restore, saved as running.</param>
/// <param name="destination">The destination that holds the point.</param>
/// <param name="superUser">A super user of the restored server.</param>
public sealed class NewInstanceRestoreContext(
    DaemonDbContext db,
    P4ServerInstance instance,
    RestoreRun run,
    BackupDestination destination,
    SuperUserCredentials superUser)
    : RestoreContext(db, instance, run, destination), IBuildResolutionContext, IServerSpecRenameContext,
        IServerAddressChangeContext
{
    public string ReleaseName => Instance.Release;

    public Guid? RequestedBuildId => Instance.BuildId;

    public bool PinResolvedBuild => true;

    /// <summary>
    /// Server ID of the server that was backed up, or null when it had none.
    /// </summary>
    public string? OldServerId { get; set; }

    /// <summary>
    /// The server ID of an instance is its name.
    /// </summary>
    public string NewServerId => Instance.Name;

    /// <summary>
    /// The port of the instance that was backed up, which the address of its server spec names. The port of the new
    /// instance when the point does not tell it: the address is then left as it is.
    /// </summary>
    public int OldPort { get; set; } = instance.Port;

    public int NewPort => Instance.Port;

    SuperUserCredentials ISuperUserContext.SuperUser => SuperUser ?? superUser;
}
