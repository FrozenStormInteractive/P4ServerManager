using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.PortChange;

/// <summary>
/// Moves an instance to another port: stop p4d, change the port, start p4d on it, then update the address of the server
/// spec as the super user. A p4d that did not run is not stopped first but once the spec is updated, so that it ends as
/// it began. Without a stored super user, the spec is left as it is: p4d is only started again if it ran.
/// </summary>
public sealed class PortChangeOperation(
    StopServerStep stopServer,
    SetPortStep setPort,
    StartServerStep startServer,
    UpdateServerAddressStep updateServerAddress) : IOperationDefinition<PortChangeContext>
{
    public const string OperationKind = "change-port";

    public string Kind => OperationKind;

    public OperationPlan<PortChangeContext> Plan(PortChangeContext context)
    {
        OperationPlan<PortChangeContext> plan = new OperationPlan<PortChangeContext>()
            .ThenIf(context.WasRunning, stopServer)
            .Then(setPort);
        return context.HasSuperUser
            ? plan.Then(startServer).Then(updateServerAddress).ThenIf(!context.WasRunning, stopServer)
            : plan.ThenIf(context.WasRunning, startServer);
    }
}

/// <summary>
/// State shared by the steps of the change of the port of one instance.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/>. Disposed once the change ends.</param>
/// <param name="instance">The instance, ready, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
/// <param name="wasRunning">Whether p4d ran before the change.</param>
/// <param name="newPort">Port to move p4d to.</param>
/// <param name="superUser">
/// Stored super user, who updates the address of the server spec, or null when none is usable: the spec is then left as
/// it is.
/// </param>
public sealed class PortChangeContext(
    DaemonDbContext db,
    P4ServerInstance instance,
    bool wasRunning,
    int newPort,
    SuperUserCredentials? superUser) : InstanceOperationContext(db, instance), IServerAddressChangeContext
{
    public bool WasRunning { get; } = wasRunning;

    /// <summary>
    /// Port before the change.
    /// </summary>
    public int OldPort { get; } = instance.Port;

    public int NewPort { get; } = newPort;

    public bool HasSuperUser => superUser is not null;

    public SuperUserCredentials SuperUser => superUser
        ?? throw new InvalidOperationException("No super user is stored for this instance.");
}
