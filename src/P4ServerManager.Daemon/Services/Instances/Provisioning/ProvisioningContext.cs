using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.Steps;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// State shared by the provisioning steps of one instance, whether it is created or imported. The instance is pinned
/// to its build as soon as it is resolved: the next steps run that build.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/>.</param>
/// <param name="instance">The instance, in the Provisioning state.</param>
public abstract class ProvisioningContext(DaemonDbContext db, P4ServerInstance instance)
    : InstanceOperationContext(db, instance), IBuildResolutionContext
{
    public string ReleaseName => Instance.Release;

    public Guid? RequestedBuildId => Instance.BuildId;

    public bool PinResolvedBuild => true;
}

/// <summary>
/// State shared by the steps that create a new server.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/>.</param>
/// <param name="instance">The instance, in the Provisioning state.</param>
/// <param name="superUser">Super user to create.</param>
public sealed class CreateInstanceContext(DaemonDbContext db, P4ServerInstance instance, SuperUserCredentials superUser)
    : ProvisioningContext(db, instance), ISuperUserContext
{
    public SuperUserCredentials SuperUser { get; } = superUser;
}

/// <summary>
/// State shared by the steps that take over an existing root.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/>.</param>
/// <param name="instance">The instance, in the Provisioning state.</param>
/// <param name="checkpointPath">Checkpoint saved before anything writes to the database.</param>
public sealed class ImportInstanceContext(DaemonDbContext db, P4ServerInstance instance, string checkpointPath)
    : ProvisioningContext(db, instance), ICheckpointContext
{
    public string CheckpointPath { get; } = checkpointPath;
}
