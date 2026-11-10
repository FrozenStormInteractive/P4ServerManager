using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;

/// <summary>
/// Gives an instance another server ID: stop p4d, set the server ID, start p4d, then rename the server spec as the super
/// user. A p4d that did not run is not stopped first but once the spec is renamed, so that it ends as it began.
/// </summary>
public sealed class ServerIdAssignmentOperation(
    StopServerStep stopServer,
    SetServerIdStep setServerId,
    StartServerStep startServer,
    RenameServerSpecStep renameServerSpec) : IOperationDefinition<ServerIdAssignmentContext>
{
    public const string OperationKind = "change-server-id";

    public string Kind => OperationKind;

    public OperationPlan<ServerIdAssignmentContext> Plan(ServerIdAssignmentContext context)
    {
        return new OperationPlan<ServerIdAssignmentContext>()
            .ThenIf(context.WasRunning, stopServer)
            .Then(setServerId)
            .Then(startServer)
            .Then(renameServerSpec)
            .ThenIf(!context.WasRunning, stopServer);
    }
}

/// <summary>
/// State shared by the steps of the change of the server ID of one instance.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/>. Disposed once the change ends.</param>
/// <param name="instance">The instance, ready, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
/// <param name="wasRunning">
/// Whether p4d ran before the change. It is started in any case, to rename the server spec, then stopped again unless it
/// ran.
/// </param>
/// <param name="newServerId">Server ID to set.</param>
/// <param name="superUser">Stored super user, who renames the server spec.</param>
public sealed class ServerIdAssignmentContext(
    DaemonDbContext db,
    P4ServerInstance instance,
    bool wasRunning,
    string newServerId,
    SuperUserCredentials superUser) : InstanceOperationContext(db, instance), ISuperUserContext
{
    public bool WasRunning { get; } = wasRunning;

    /// <summary>
    /// Server ID before the change, as p4d tells it once stopped, or null when it had none. Set by
    /// <see cref="SetServerIdStep"/>, before it sets the new one.
    /// </summary>
    public string? OldServerId { get; set; }

    public string NewServerId { get; } = newServerId;

    public SuperUserCredentials SuperUser { get; } = superUser;

    public P4ServerBuild Build { get; } = instance.Build
                                          ?? throw new ArgumentException("The instance has no p4d build.", nameof(instance));
}
