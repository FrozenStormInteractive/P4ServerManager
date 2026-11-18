using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups;
using P4ServerManager.Daemon.Services.Backups.Destinations;
using P4ServerManager.Daemon.Services.Backups.Restore;
using P4ServerManager.Daemon.Services.Backups.Verification;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.Deletion;
using P4ServerManager.Daemon.Services.Instances.HealthChecks;
using P4ServerManager.Daemon.Services.Instances.PortChange;
using P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Instances.UnicodeConversion;
using P4ServerManager.Daemon.Services.Instances.Upgrade;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Keeps track of the p4d processes. At startup it adopts the instances that still run, without restarting them, then
/// starts the stopped ones marked for auto-start and checks them periodically. It also starts, stops, upgrades, converts
/// to Unicode mode, checks the health of, changes the server ID or the port of and deletes them on request.
/// </summary>
public sealed partial class InstanceSupervisor(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    P4ServerLauncher launcher,
    OperationRunner operationRunner,
    UpgradeOperation upgradeOperation,
    UnicodeConversionOperation unicodeConversionOperation,
    ServerIdAssignmentOperation serverIdAssignmentOperation,
    PortChangeOperation portChangeOperation,
    SuperUserService superUserService,
    HealthCheckRunner healthCheckRunner,
    BackupOperation backupOperation,
    VerifyBackupOperation verifyBackupOperation,
    BackupRunNotifier backupRunNotifier,
    BackupDestinationService backupDestinationService,
    RestoreOperation restoreOperation,
    RestoreRunNotifier restoreRunNotifier,
    InstanceRuntimeRegistry runtimeRegistry,
    OperationTracker operationTracker,
    InstanceNotifier notifier,
    DaemonPaths paths,
    InstanceFilePlanner filePlanner,
    TimeProvider timeProvider,
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
        await EndLeftRestoresAsync(cancellationToken);
        serversLeftStopped = await healthCheckRunner.EndLeftRunsAsync(cancellationToken);
        await EndLeftBackupsAsync(cancellationToken);
        await ResumeDeletionsAsync(cancellationToken);
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

    /// <summary>
    /// Enables the maintenance mode of an instance and makes its p4d run in it (<c>p4d -n</c>): a running p4d is restarted, and
    /// the instance is <see cref="InstanceRuntimeStatus.Restarting"/> meanwhile; a stopped one is started, and the
    /// instance is <see cref="InstanceRuntimeStatus.Starting"/> meanwhile. The mode is kept: every later start of p4d
    /// follows it, until <see cref="DisableMaintenanceModeAsync"/>.
    /// </summary>
    /// <exception cref="InstanceConflictException">
    /// The instance is not ready, another operation is in progress, or p4d already runs in maintenance mode.
    /// </exception>
    public async Task<P4ServerInstance> EnableMaintenanceModeAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(instanceId, cancellationToken, async (db, instance) =>
        {
            EnsureReady(instance);
            bool running = runtimeRegistry.Get(instance.Id) == InstanceRuntimeStatus.Running;
            if (instance.MaintenanceMode && running)
            {
                throw new InstanceConflictException($"Instance {instance.Name} already runs in maintenance mode.");
            }

            instance.MaintenanceMode = true;
            await TransitionAsync(
                db,
                instance,
                running ? InstanceRuntimeStatus.Restarting : InstanceRuntimeStatus.Starting,
                async stoppingToken =>
                {
                    await launcher.StopAsync(instance, stoppingToken);
                    await launcher.StartAsync(instance, stoppingToken);
                });
            logger.LogInformation("Enabled the maintenance mode of instance {Name}", instance.Name);
        });
    }

    /// <summary>
    /// Disables the maintenance mode of an instance. A running p4d is restarted without <c>-n</c>, and the instance is
    /// <see cref="InstanceRuntimeStatus.Restarting"/> meanwhile. A stopped one stays stopped: its next start is a normal
    /// one.
    /// </summary>
    /// <exception cref="InstanceConflictException">
    /// The instance is not ready, another operation is in progress, or its maintenance mode is not enabled.
    /// </exception>
    public async Task<P4ServerInstance> DisableMaintenanceModeAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(instanceId, cancellationToken, async (db, instance) =>
        {
            EnsureReady(instance);
            if (!instance.MaintenanceMode)
            {
                throw new InstanceConflictException($"Instance {instance.Name} is not in maintenance mode.");
            }

            instance.MaintenanceMode = false;
            if (runtimeRegistry.Get(instance.Id) == InstanceRuntimeStatus.Running)
            {
                await TransitionAsync(db, instance, InstanceRuntimeStatus.Restarting, async stoppingToken =>
                {
                    await launcher.StopAsync(instance, stoppingToken);
                    await launcher.StartAsync(instance, stoppingToken);
                });
            }
            else
            {
                await db.SaveChangesAsync(CancellationToken.None);
                await notifier.InstanceChangedAsync(instance);
            }
            logger.LogInformation("Disabled the maintenance mode of instance {Name}", instance.Name);
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

    /// <summary>
    /// Runs an operation on an instance that the caller holds. The operation is begun before the first await, so its
    /// snapshot replaces the previous one as soon as this returns; <paramref name="onBegun"/> is then called, to log it.
    /// The shutdown of the daemon is let through; any other failure is returned, for the caller to keep it as the last
    /// error of the instance and to leave p4d as it should be.
    /// </summary>
    /// <returns>The failure of the operation, or null when it succeeded.</returns>
    private async Task<Exception?> RunOperationAsync<TContext>(
        IOperationDefinition<TContext> operation,
        TContext context,
        Action onBegun,
        CancellationToken cancellationToken)
        where TContext : InstanceOperationContext
    {
        try
        {
            Task run = operationRunner.RunAsync(context.Instance.Id, operation, context, cancellationToken);
            onBegun();
            await run;
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Starts or stops p4d after a failed operation, so that it runs as it did before. It is left alone when it already
    /// does. A failure is added to the last error of the instance, which is not saved: the caller saves it.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="running">Whether p4d should run.</param>
    /// <param name="cancellationToken">The shutdown of the daemon.</param>
    private async Task RestoreRunningStateAsync(
        P4ServerInstance instance,
        bool running,
        CancellationToken cancellationToken)
    {
        if (launcher.IsRunning(instance) == running)
        {
            return;
        }

        string action = running ? "started" : "stopped";
        try
        {
            if (running)
            {
                await launcher.StartAsync(instance, cancellationToken);
            }
            else
            {
                await launcher.StopAsync(instance, cancellationToken);
            }
            logger.LogInformation("Instance {Name} {Action} again", instance.Name, action);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not leave instance {Name} {Action} again", instance.Name, action);
            instance.LastError += $" It could not be {action} again: {exception.Message}";
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

    private InstanceRuntimeStatus ReadRuntimeStatus(P4ServerInstance instance)
    {
        return launcher.IsRunning(instance) ? InstanceRuntimeStatus.Running : InstanceRuntimeStatus.Stopped;
    }

    private SemaphoreSlim GetLock(Guid instanceId)
    {
        return instanceLocks.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
    }
}
