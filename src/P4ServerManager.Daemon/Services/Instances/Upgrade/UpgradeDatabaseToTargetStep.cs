using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

/// <summary>
/// Pins the instance to the target build, then upgrades the database with it. The instance is pinned first: once p4d
/// has begun to upgrade the database, the previous build may no longer open it.
/// </summary>
public sealed class UpgradeDatabaseToTargetStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    InstanceNotifier notifier) : IOperationStep<UpgradeContext>
{
    public string Key => "upgrade-database";

    public string Title => "Upgrade the database";

    public bool ShouldRun(UpgradeContext context)
    {
        return !context.UpToDate;
    }

    public async Task<string?> RunAsync(
        UpgradeContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        P4ServerBuild build = context.TargetBuild
            ?? throw new InvalidOperationException("The target build is not resolved.");

        // Read from the cache of the builds: not tracked by this context yet.
        context.Db.Builds.Attach(build);
        instance.Build = build;
        instance.Release = context.Release.Name;
        await context.Db.SaveChangesAsync(cancellationToken);
        context.DatabaseUpgradeStarted = true;
        await notifier.InstanceChangedAsync(instance);

        // Not canceled once begun: a p4d killed while it rewrites the tables can leave the database unusable.
        await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(build),
                [.. P4ServerLauncher.GetRootArguments(instance), "-xu"],
                Timeout: SaveCheckpointStep.Timeout),
            CancellationToken.None);
        context.DatabaseUpgraded = true;
        return $"p4d {build.Version}/{build.BuildNumber}";
    }
}
