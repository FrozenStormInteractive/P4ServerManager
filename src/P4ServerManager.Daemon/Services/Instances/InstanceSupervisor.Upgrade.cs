using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Upgrade;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances;

// Upgrade of the instances to another p4d build.
public sealed partial class InstanceSupervisor
{
    /// <summary>
    /// Moves an instance to the latest build of <paramref name="release"/>, or to an exact build of it from the archive:
    /// the same release for a newer patch, or a newer release. The upgrade runs in the background, once this returns: the instance stays
    /// <see cref="InstanceRuntimeStatus.Upgrading"/> until it ends, and its operation tells its progress. Only the
    /// shutdown of the daemon cancels it, and never while p4d upgrades the database.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="release">Release to move to.</param>
    /// <param name="build">Build number of <paramref name="release"/> in the archive, or null for its latest build.</param>
    /// <param name="cancellationToken">Aborts the wait for the instance to be free.</param>
    /// <returns>The instance, once its upgrade has begun.</returns>
    /// <exception cref="InstanceConflictException">
    /// The instance is not ready, another operation is in progress, or the release or the build is older than the one
    /// of the instance: p4d cannot downgrade a database.
    /// </exception>
    public async Task<P4ServerInstance> UpgradeInstanceAsync(
        Guid instanceId,
        P4ServerRelease release,
        string? build,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim? instanceLock = await AcquireLockAsync(instanceId, cancellationToken);
        DaemonDbContext? db = null;
        try
        {
            db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await FindAsync(db, instanceId, cancellationToken);
            EnsureReady(instance);
            if (instance.Build is null)
            {
                throw new InstanceConflictException($"Instance {instance.Name} has no p4d build yet.");
            }
            if (P4ServerRelease.TryParse(instance.Release, out P4ServerRelease current) && release < current)
            {
                throw new InstanceConflictException(
                    $"Instance {instance.Name} runs {current}: its database cannot go back to {release}.");
            }

            Guid? requestedBuildId = null;
            if (build is not null)
            {
                // Not tracked: the upgrade attaches the build it gets from the build cache.
                P4ServerBuild target = await db.Builds.AsNoTracking().FirstOrDefaultAsync(
                    b => b.Release == release.Name && b.BuildNumber == build,
                    cancellationToken)
                    ?? throw new InstanceConflictException($"Build {build} of {release} is not in the archive.");
                if (P4ServerBuildNumber.Compare(target, instance.Build) < 0)
                {
                    throw new InstanceConflictException(
                        $"Instance {instance.Name} runs p4d {instance.Build.Version}/{instance.Build.BuildNumber}: "
                        + $"it cannot go back to {target.Version}/{target.BuildNumber}.");
                }
                requestedBuildId = target.Id;
            }

            UpgradeContext context = new(db, instance, release, launcher.IsRunning(instance), requestedBuildId);
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.Upgrading);
            await notifier.InstanceChangedAsync(instance);

            // The upgrade outlives the request: it releases the instance and the context once it ends.
            _ = RunInBackgroundAsync(
                db,
                instance,
                instanceLock,
                stoppingToken => UpgradeAsync(context, stoppingToken));
            instanceLock = null;
            db = null;
            return instance;
        }
        finally
        {
            if (db is not null)
            {
                await db.DisposeAsync();
            }
            instanceLock?.Release();
        }
    }

    /// <summary>
    /// Runs the upgrade. A failure is kept as the last error of the instance, which is not saved: the caller saves it.
    /// p4d is started again on its previous build if it ran and the database upgrade had not begun.
    /// </summary>
    private async Task UpgradeAsync(UpgradeContext context, CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        Exception? failure = await RunOperationAsync(
            upgradeOperation,
            context,
            () => logger.LogInformation(
                "Upgrading instance {Name} from p4d {Version}/{Build} to {Release}",
                instance.Name,
                context.PreviousBuild.Version,
                context.PreviousBuild.BuildNumber,
                context.Release),
            cancellationToken);
        if (failure is null)
        {
            if (context.UpToDate)
            {
                // Nothing was done: the last error of an earlier operation is kept.
                logger.LogInformation(
                    "Instance {Name} already runs p4d {Version}/{Build}",
                    instance.Name,
                    instance.Build?.Version,
                    instance.Build?.BuildNumber);
                return;
            }

            instance.LastError = null;
            logger.LogInformation(
                "Upgraded instance {Name} to p4d {Version}/{Build}",
                instance.Name,
                instance.Build?.Version,
                instance.Build?.BuildNumber);
            return;
        }

        logger.LogError(failure, "Upgrade of instance {Name} failed", instance.Name);
        instance.LastError = context.DatabaseUpgradeStarted && !context.DatabaseUpgraded
            ? $"{failure.Message} The database may be partly upgraded."
            : failure.Message;
        if (!context.DatabaseUpgradeStarted && context.WasRunning)
        {
            await RestoreRunningStateAsync(instance, running: true, cancellationToken);
        }
    }
}
