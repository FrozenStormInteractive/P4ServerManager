using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.UnicodeConversion;

namespace P4ServerManager.Daemon.Services.Instances;

// Conversion of the instances to Unicode mode.
public sealed partial class InstanceSupervisor
{
    /// <summary>
    /// Converts an instance to Unicode mode. It cannot be undone. The conversion runs in the background, once this
    /// returns: the instance stays <see cref="InstanceRuntimeStatus.ConvertingToUnicode"/> until it ends, and its
    /// operation tells its progress. Only the shutdown of the daemon cancels it, and never while p4d switches the
    /// database.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="cancellationToken">Aborts the wait for the instance to be free.</param>
    /// <returns>The instance, once its conversion has begun.</returns>
    /// <exception cref="InstanceConflictException">
    /// The instance is not ready, another operation is in progress, or it already runs in Unicode mode.
    /// </exception>
    public async Task<P4ServerInstance> ConvertInstanceToUnicodeAsync(
        Guid instanceId,
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
            if (instance.Unicode)
            {
                throw new InstanceConflictException($"Instance {instance.Name} already runs in Unicode mode.");
            }

            UnicodeConversionContext context = new(
                db,
                instance,
                launcher.IsRunning(instance),
                paths.GetUnicodeCheckpointPath(instance.Id, timeProvider.GetUtcNow()));
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.ConvertingToUnicode);
            await notifier.InstanceChangedAsync(instance);

            // The conversion outlives the request: it releases the instance and the context once it ends.
            _ = RunInBackgroundAsync(
                db,
                instance,
                instanceLock,
                stoppingToken => ConvertToUnicodeAsync(context, stoppingToken));
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
    /// Runs the conversion. A failure is kept as the last error of the instance, which is not saved: the caller saves it.
    /// p4d is started again if it ran and the database was not converted: <c>p4d -xi</c> checks the metadata before it
    /// changes anything.
    /// </summary>
    private async Task ConvertToUnicodeAsync(UnicodeConversionContext context, CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        Exception? failure = await RunOperationAsync(
            unicodeConversionOperation,
            context,
            () => logger.LogInformation("Converting instance {Name} to Unicode mode", instance.Name),
            cancellationToken);
        if (failure is null)
        {
            instance.LastError = null;
            logger.LogInformation("Converted instance {Name} to Unicode mode", instance.Name);
            return;
        }

        logger.LogError(failure, "Conversion of instance {Name} to Unicode mode failed", instance.Name);
        instance.LastError = failure.Message;
        if (context.WasRunning && !instance.Unicode)
        {
            await RestoreRunningStateAsync(instance, running: true, cancellationToken);
        }
    }
}
