using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Keeps track of the p4d processes. At startup it adopts the instances that still run, without restarting them, then
/// checks them periodically. It also starts, stops and deletes them on request.
/// </summary>
public sealed class InstanceSupervisor(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    P4ServerLauncher launcher,
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

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await AdoptAsync(cancellationToken);
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

        try
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await db.Instances
                .Include(i => i.Build)
                .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken)
                ?? throw new KeyNotFoundException($"Instance {instanceId} does not exist.");

            await operation(db, instance);
            return instance;
        }
        finally
        {
            instanceLock.Release();
        }
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
