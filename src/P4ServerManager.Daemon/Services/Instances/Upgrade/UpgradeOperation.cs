using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

/// <summary>
/// Moves an instance to another p4d build: get the build while p4d still runs, stop p4d, upgrade the database, start
/// p4d again. A server stopped on purpose stays stopped, and an instance that already runs the target build is left as
/// it is.
/// </summary>
public sealed class UpgradeOperation(
    ResolveBuildStep resolveBuild,
    StopServerStep stopServer,
    UpgradeDatabaseStep upgradeDatabase,
    StartServerStep startServer) : IOperationDefinition<UpgradeContext>
{
    public const string OperationKind = "upgrade";

    public string Kind => OperationKind;

    public OperationPlan<UpgradeContext> Plan(UpgradeContext context)
    {
        return new OperationPlan<UpgradeContext>()
            .Then(resolveBuild)
            .Then(stopServer, skipWhen: c => !c.WasRunning || c.UpToDate)
            .Then(upgradeDatabase, skipWhen: c => c.UpToDate)
            .Then(startServer, skipWhen: c => !c.WasRunning || c.UpToDate);
    }
}
