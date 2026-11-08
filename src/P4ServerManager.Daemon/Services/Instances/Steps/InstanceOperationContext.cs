using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Steps;

/// <summary>
/// What every operation on an instance gives its steps. A step typed on it fits the plan of any of these operations.
/// </summary>
public interface IInstanceOperationContext : IOperationContext
{
    /// <summary>
    /// Context that tracks <see cref="Instance"/>.
    /// </summary>
    DaemonDbContext Db { get; }

    P4ServerInstance Instance { get; }
}

/// <summary>
/// Context of an operation that gets a p4d build with the <see cref="ResolveBuildStep"/>.
/// </summary>
public interface IBuildResolutionContext : IInstanceOperationContext
{
    /// <summary>
    /// Release whose latest build is resolved, unless <see cref="RequestedBuildId"/> is set.
    /// </summary>
    string ReleaseName { get; }

    /// <summary>
    /// Exact build to resolve, if any.
    /// </summary>
    Guid? RequestedBuildId { get; }

    /// <summary>
    /// Whether the instance is pinned to the build as soon as it is resolved. Otherwise a later step pins it.
    /// </summary>
    bool PinResolvedBuild { get; }
}

/// <summary>
/// Context of an operation that saves a checkpoint with the <see cref="SaveCheckpointStep"/>.
/// </summary>
public interface ICheckpointContext : IInstanceOperationContext
{
    /// <summary>
    /// Compressed checkpoint to write.
    /// </summary>
    string CheckpointPath { get; }
}

/// <summary>
/// Context of an operation that runs p4 commands as the super user of the instance.
/// </summary>
public interface ISuperUserContext : IInstanceOperationContext
{
    SuperUserCredentials SuperUser { get; }
}

/// <summary>
/// Base of the contexts of the operations on an instance.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/>.</param>
/// <param name="instance">The instance.</param>
public abstract class InstanceOperationContext(DaemonDbContext db, P4ServerInstance instance)
    : IInstanceOperationContext
{
    public DaemonDbContext Db { get; } = db;

    public P4ServerInstance Instance { get; } = instance;

    public OperationValues Values { get; } = new();
}
