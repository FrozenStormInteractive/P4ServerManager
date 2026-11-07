using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.HealthChecks;
using P4ServerManager.Daemon.Services.Instances.UnicodeConversion;
using P4ServerManager.Daemon.Services.Instances.Upgrade;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Keeps track of the p4d processes. At startup it adopts the instances that still run, without restarting them, then
/// starts the stopped ones marked for auto-start and checks them periodically. It also starts, stops, upgrades, converts
/// to Unicode mode, checks the health of and deletes them on request.
/// </summary>
public sealed class InstanceSupervisor(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    P4ServerLauncher launcher,
    InstanceUpgrader upgrader,
    InstanceUnicodeConverter unicodeConverter,
    HealthCheckRunner healthCheckRunner,
    InstanceRuntimeRegistry runtimeRegistry,
    OperationTracker operationTracker,
    InstanceNotifier notifier,
    DaemonPaths paths,
    IHostApplicationLifetime lifetime,
    ILogger<InstanceSupervisor> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    // The lock of a deleted instance is kept: a caller may still wait on it.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> instanceLocks = new();

    // The servers a health check left stopped when the previous daemon ended. They start with the others.
    private IReadOnlyList<Guid> serversLeftStopped = [];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await AdoptAsync(cancellationToken);
        serversLeftStopped = await healthCheckRunner.EndLeftRunsAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public async Task<P4ServerInstance> StartInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(instanceId, cancellationToken, async (db, instance) =>
        {
            EnsureReady(instance);
            if (runtimeRegistry.Get(instance.Id) == InstanceRuntimeStatus.Running)
            {
                throw new InstanceConflictException($"Instance {instance.Name} is already running.");
            }

            await TransitionAsync(db, instance, InstanceRuntimeStatus.Starting, async stoppingToken =>
            {
                await launcher.StartAsync(instance, stoppingToken);
            });
        });
    }

    /// <summary>
    /// Stops the p4d of an instance if it runs, then starts it again. The instance stays
    /// <see cref="InstanceRuntimeStatus.Restarting"/> from the stop to the start.
    /// </summary>
    public async Task<P4ServerInstance> RestartInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(instanceId, cancellationToken, async (db, instance) =>
        {
            EnsureReady(instance);
            await TransitionAsync(db, instance, InstanceRuntimeStatus.Restarting, async stoppingToken =>
            {
                await launcher.StopAsync(instance, stoppingToken);
                await launcher.StartAsync(instance, stoppingToken);
            });
        });
    }

    public async Task<P4ServerInstance> StopInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(instanceId, cancellationToken, async (db, instance) =>
        {
            EnsureReady(instance);
            if (runtimeRegistry.Get(instance.Id) != InstanceRuntimeStatus.Running)
            {
                throw new InstanceConflictException($"Instance {instance.Name} is not running.");
            }

            await TransitionAsync(db, instance, InstanceRuntimeStatus.Stopping, async stoppingToken =>
            {
                await launcher.StopAsync(instance, stoppingToken);
            });
        });
    }

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
                stoppingToken => upgrader.UpgradeAsync(context, stoppingToken));
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

            UnicodeConversionContext context = new(db, instance, launcher.IsRunning(instance));
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.ConvertingToUnicode);
            await notifier.InstanceChangedAsync(instance);

            // The conversion outlives the request: it releases the instance and the context once it ends.
            _ = RunInBackgroundAsync(
                db,
                instance,
                instanceLock,
                stoppingToken => unicodeConverter.ConvertAsync(context, stoppingToken));
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
    /// Begins a health check of an instance. It runs in the background, once this returns: p4d is stopped if it runs, its
    /// database is checked, then p4d is started again if it ran. The instance stays
    /// <see cref="InstanceRuntimeStatus.CheckingHealth"/> until it ends, so that nothing starts p4d or changes the database
    /// meanwhile. Only a cancellation of the run or the shutdown of the daemon stops it.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="checks">The checks to run.</param>
    /// <param name="cancellationToken">Aborts the wait for the instance to be free.</param>
    /// <returns>The run, once it has begun.</returns>
    /// <exception cref="InstanceConflictException">
    /// The instance is not ready, or another operation is in progress.
    /// </exception>
    public async Task<HealthCheckRunDto> StartHealthCheckAsync(
        Guid instanceId,
        IReadOnlyList<HealthCheckKind> checks,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim? instanceLock = await AcquireLockAsync(instanceId, cancellationToken);
        DaemonDbContext? db = null;
        try
        {
            db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await FindAsync(db, instanceId, cancellationToken);
            EnsureReady(instance);

            HealthCheckRunDto run = await healthCheckRunner.BeginAsync(instance, checks, cancellationToken);
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.CheckingHealth);
            await notifier.InstanceChangedAsync(instance);

            // The check outlives the request: it releases the instance and the context once it ends.
            _ = RunInBackgroundAsync(
                db,
                instance,
                instanceLock,
                stoppingToken => Task.Run(() => healthCheckRunner.RunAsync(instance, stoppingToken), CancellationToken.None));
            instanceLock = null;
            db = null;
            return run;
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
    /// Stops the p4d of an instance if it runs, then removes the instance. The instance stays
    /// <see cref="InstanceRuntimeStatus.Deleting"/> meanwhile. A failed instance can be deleted, but not one that is
    /// being provisioned. Once begun, the operation runs to its end: only the shutdown of the daemon cancels it.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="deleteFiles">
    /// Also deletes the directory of the instance in the data directory: its logs, its client state and the default
    /// root. A root chosen at creation is always kept: it may hold files the daemon did not create.
    /// </param>
    /// <param name="cancellationToken">Aborts the wait for the instance to be free.</param>
    /// <returns>The instance as it was before it was deleted.</returns>
    public async Task<P4ServerInstance> DeleteInstanceAsync(
        Guid instanceId,
        bool deleteFiles,
        CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(instanceId, cancellationToken, async (db, instance) =>
        {
            if (instance.State == P4ServerInstanceState.Provisioning)
            {
                throw new InstanceConflictException($"Instance {instance.Name} is being provisioned.");
            }

            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.Deleting);
            await notifier.InstanceChangedAsync(instance);
            bool deleted = false;
            try
            {
                await launcher.StopAsync(instance, lifetime.ApplicationStopping);
                if (deleteFiles)
                {
                    DeleteFiles(instance);
                }

                // Out of the change tracker, so that a failure here leaves the entity as it was.
                await db.Instances.Where(i => i.Id == instance.Id).ExecuteDeleteAsync(CancellationToken.None);
                deleted = true;
            }
            catch (InstanceOperationException exception)
            {
                instance.LastError = exception.Message;
                throw;
            }
            finally
            {
                if (!deleted)
                {
                    runtimeRegistry.Set(instance.Id, ReadRuntimeStatus(instance));
                    await db.SaveChangesAsync(CancellationToken.None);
                    await notifier.InstanceChangedAsync(instance);
                }
            }

            runtimeRegistry.Remove(instance.Id);
            operationTracker.Remove(instance.Id);
            await notifier.InstanceDeletedAsync(instance.Id);
            logger.LogInformation("Deleted instance {Name}", instance.Name);
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(CheckInterval);
        try
        {
            await AutoStartAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CheckInstancesAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The daemon stops. The p4d processes keep running.
        }
    }

    private async Task AdoptAsync(CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<P4ServerInstance> instances = await db.Instances.Include(i => i.Build).ToListAsync(cancellationToken);
        foreach (P4ServerInstance instance in instances)
        {
            switch (instance.State)
            {
                case P4ServerInstanceState.Provisioning:
                    // The provisioning ran in the previous daemon process and cannot resume: the super user password
                    // only lived in its memory.
                    instance.State = P4ServerInstanceState.Failed;
                    instance.LastError = "The daemon restarted during the provisioning.";
                    runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.Stopped);
                    logger.LogWarning("Instance {Name} was left half provisioned", instance.Name);
                    break;
                case P4ServerInstanceState.Ready:
                    int? pid = launcher.GetRunningPid(instance);
                    runtimeRegistry.Set(
                        instance.Id,
                        pid is null ? InstanceRuntimeStatus.Stopped : InstanceRuntimeStatus.Running);
                    if (pid is not null)
                    {
                        logger.LogInformation("Adopted running instance {Name} (pid {Pid})", instance.Name, pid);
                    }
                    else
                    {
                        logger.LogInformation("Instance {Name} is stopped", instance.Name);
                    }
                    break;
                case P4ServerInstanceState.Failed:
                    runtimeRegistry.Set(instance.Id, ReadRuntimeStatus(instance));
                    break;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Starts the ready instances marked for auto-start whose p4d was not adopted, and the ones a health check left
    /// stopped when the previous daemon ended. It runs once, after the adoption and
    /// outside the startup of the host, so that the API answers while p4d starts. A failed start is logged and kept as
    /// the last error of the instance; it does not stop the other starts.
    /// </summary>
    private async Task AutoStartAsync(CancellationToken stoppingToken)
    {
        List<Guid> instanceIds;
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(stoppingToken))
        {
            instanceIds = await db.Instances
                .Where(i => i.State == P4ServerInstanceState.Ready && (i.AutoStart || serversLeftStopped.Contains(i.Id)))
                .Select(i => i.Id)
                .ToListAsync(stoppingToken);
        }

        await Task.WhenAll(instanceIds
            .Where(id => runtimeRegistry.Get(id) == InstanceRuntimeStatus.Stopped)
            .Select(id => AutoStartInstanceAsync(id, stoppingToken)));
    }

    private async Task AutoStartInstanceAsync(Guid instanceId, CancellationToken stoppingToken)
    {
        try
        {
            P4ServerInstance instance = await StartInstanceAsync(instanceId, stoppingToken);
            logger.LogInformation("Auto-started instance {Name}", instance.Name);
        }
        catch (Exception exception) when (exception is InstanceConflictException or KeyNotFoundException)
        {
            // Started, stopped, changed or deleted by a request in the meantime.
            logger.LogDebug(exception, "Skipped the auto-start of instance {Id}", instanceId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any failure is caught: one escaping ExecuteAsync would stop the daemon.
            logger.LogError(exception, "Could not auto-start instance {Id}", instanceId);
        }
    }

    /// <summary>
    /// Runs an operation that outlives the request that began it, then reads the status back from the process and saves
    /// the instance, whatever the outcome. It releases the instance and disposes of the context once it ends.
    /// </summary>
    private async Task RunInBackgroundAsync(
        DaemonDbContext db,
        P4ServerInstance instance,
        SemaphoreSlim instanceLock,
        Func<CancellationToken, Task> operation)
    {
        try
        {
            await operation(lifetime.ApplicationStopping);
        }
        catch (Exception exception)
        {
            // Any failure is caught: nothing awaits this task.
            logger.LogWarning(exception, "The operation on instance {Name} was interrupted", instance.Name);
        }

        try
        {
            runtimeRegistry.Set(instance.Id, ReadRuntimeStatus(instance));
            await db.SaveChangesAsync(CancellationToken.None);
            await notifier.InstanceChangedAsync(instance);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not save instance {Name} after its operation", instance.Name);
        }
        finally
        {
            await db.DisposeAsync();
            instanceLock.Release();
        }
    }

    private async Task CheckInstancesAsync(CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<P4ServerInstance> instances = await db.Instances
            .AsNoTracking()
            .Include(i => i.Build)
            .Where(i => i.State == P4ServerInstanceState.Ready)
            .ToListAsync(cancellationToken);
        foreach (P4ServerInstance instance in instances)
        {
            SemaphoreSlim instanceLock = GetLock(instance.Id);
            if (!await instanceLock.WaitAsync(0, cancellationToken))
            {
                // A start or a stop is in progress: it reports the status itself.
                continue;
            }

            try
            {
                if (!await db.Instances.AnyAsync(i => i.Id == instance.Id, cancellationToken))
                {
                    // Deleted since the list was read.
                    continue;
                }

                InstanceRuntimeStatus status = ReadRuntimeStatus(instance);
                InstanceRuntimeStatus previous = runtimeRegistry.Get(instance.Id);
                if (runtimeRegistry.Set(instance.Id, status))
                {
                    if (previous == InstanceRuntimeStatus.Running)
                    {
                        logger.LogWarning("p4d of instance {Name} stopped outside the daemon", instance.Name);
                    }
                    await notifier.InstanceChangedAsync(instance);
                }
            }
            finally
            {
                instanceLock.Release();
            }
        }
    }

    /// <summary>
    /// Runs a start, a stop or a restart. The transitional <paramref name="status"/> is pushed to every client first,
    /// so that it survives a reload of the page. Once begun, the operation runs to its end even if the request that
    /// asked for it is aborted: only the shutdown of the daemon cancels it. The status is then read back from the
    /// process, whatever the outcome.
    /// </summary>
    private async Task TransitionAsync(
        DaemonDbContext db,
        P4ServerInstance instance,
        InstanceRuntimeStatus status,
        Func<CancellationToken, Task> operation)
    {
        runtimeRegistry.Set(instance.Id, status);
        await notifier.InstanceChangedAsync(instance);
        try
        {
            await operation(lifetime.ApplicationStopping);
            instance.LastError = null;
        }
        catch (InstanceOperationException exception)
        {
            instance.LastError = exception.Message;
            throw;
        }
        finally
        {
            runtimeRegistry.Set(instance.Id, ReadRuntimeStatus(instance));
            await db.SaveChangesAsync(CancellationToken.None);
            await notifier.InstanceChangedAsync(instance);
        }
    }

    private async Task<P4ServerInstance> RunExclusiveAsync(
        Guid instanceId,
        CancellationToken cancellationToken,
        Func<DaemonDbContext, P4ServerInstance, Task> operation)
    {
        SemaphoreSlim instanceLock = await AcquireLockAsync(instanceId, cancellationToken);
        try
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await FindAsync(db, instanceId, cancellationToken);
            await operation(db, instance);
            return instance;
        }
        finally
        {
            instanceLock.Release();
        }
    }

    /// <summary>
    /// Takes the lock of an instance, which every operation on it holds.
    /// </summary>
    /// <exception cref="InstanceConflictException">Another operation is in progress on the instance.</exception>
    private async Task<SemaphoreSlim> AcquireLockAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        SemaphoreSlim instanceLock = GetLock(instanceId);
        if (!await instanceLock.WaitAsync(0, cancellationToken))
        {
            if (runtimeRegistry.Get(instanceId).IsTransitional())
            {
                throw new InstanceConflictException("Another operation is in progress on this instance.");
            }

            // Only the periodic check holds the lock, for a moment.
            await instanceLock.WaitAsync(cancellationToken);
        }
        return instanceLock;
    }

    private static async Task<P4ServerInstance> FindAsync(
        DaemonDbContext db,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        return await db.Instances
            .Include(i => i.Build)
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken)
            ?? throw new KeyNotFoundException($"Instance {instanceId} does not exist.");
    }

    private static void EnsureReady(P4ServerInstance instance)
    {
        if (instance.State != P4ServerInstanceState.Ready)
        {
            throw new InstanceConflictException($"Instance {instance.Name} is not ready.");
        }
    }

    private void DeleteFiles(P4ServerInstance instance)
    {
        string directory = paths.GetInstanceDirectory(instance.Id);
        try
        {
            if (Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows())
                {
                    // p4 makes its tickets file read-only, and Windows does not delete a read-only file.
                    foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                }
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstanceOperationException(
                $"The files of instance {instance.Name} could not be deleted: {exception.Message}",
                exception);
        }

        if (instance.RootPath != paths.GetDefaultRootPath(instance.Id))
        {
            logger.LogInformation(
                "Kept the root {RootPath} of instance {Name}: it was chosen at creation",
                instance.RootPath,
                instance.Name);
        }
    }

    private InstanceRuntimeStatus ReadRuntimeStatus(P4ServerInstance instance)
    {
        return launcher.IsRunning(instance) ? InstanceRuntimeStatus.Running : InstanceRuntimeStatus.Stopped;
    }

    private SemaphoreSlim GetLock(Guid instanceId)
    {
        return instanceLocks.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
    }
}
