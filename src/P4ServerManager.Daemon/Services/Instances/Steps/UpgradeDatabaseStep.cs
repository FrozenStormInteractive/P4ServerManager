using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Steps;

/// <summary>
/// Upgrades the database to the build the <see cref="ResolveBuildStep"/> got, or to the build of the instance without
/// it. When the instance is not pinned to that build yet, it is pinned first: once p4d has begun to upgrade the
/// database, the previous build may no longer open it. A database already at that level is left as it is; p4d refuses
/// a database newer than the build.
/// </summary>
public sealed class UpgradeDatabaseStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    InstanceNotifier notifier) : IOperationStep<IInstanceOperationContext>
{
    /// <summary>
    /// Set once the instance is pinned to the target build, right before p4d upgrades the database.
    /// </summary>
    public static readonly OperationValueKey<bool> Started = new("database-upgrade-started");

    /// <summary>
    /// Set once p4d has upgraded the database.
    /// </summary>
    public static readonly OperationValueKey<bool> Completed = new("database-upgraded");

    public string Key => "upgrade-database";

    public string Title => "Upgrade the database";

    public async Task<string?> RunAsync(
        IInstanceOperationContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        if (context.Values.TryGet(ResolveBuildStep.ResolvedBuild, out P4ServerBuild? target)
            && target.Id != instance.BuildId)
        {
            // Read from the cache of the builds: not tracked by this context yet.
            context.Db.Builds.Attach(target);
            instance.Build = target;
            instance.Release = target.Release;
            await context.Db.SaveChangesAsync(cancellationToken);
            await notifier.InstanceChangedAsync(instance);
        }
        P4ServerBuild build = instance.Build
            ?? throw new InvalidOperationException("The instance has no p4d build.");
        context.Values.Set(Started, true);

        // Not canceled once begun: a p4d killed while it rewrites the tables can leave the database unusable.
        await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(build),
                [.. P4ServerLauncher.GetRootArguments(instance), "-xu"],
                Timeout: SaveCheckpointStep.Timeout),
            CancellationToken.None);
        context.Values.Set(Completed, true);
        return $"p4d {build.Version}/{build.BuildNumber}";
    }
}
