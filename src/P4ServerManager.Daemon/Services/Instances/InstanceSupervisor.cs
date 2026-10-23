using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Keeps track of the p4d processes. At startup it adopts the instances that still run, without restarting them, then
/// checks them periodically. It also starts and stops them on request.
/// </summary>
public sealed class InstanceSupervisor(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    P4ServerLauncher launcher,
    InstanceRuntimeRegistry runtimeRegistry,
    InstanceNotifier notifier,
    ILogger<InstanceSupervisor> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

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
            if (runtimeRegistry.Get(instance.Id) == InstanceRuntimeStatus.Running)
            {
                throw new InstanceConflictException($"Instance {instance.Name} is already running.");
            }

            try
            {
                await launcher.StartAsync(instance, cancellationToken);
                instance.LastError = null;
                runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.Running);
            }
            catch (InstanceOperationException exception)
            {
                instance.LastError = exception.Message;
                runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.Stopped);
                throw;
            }
            finally
            {
                await db.SaveChangesAsync(CancellationToken.None);
                await notifier.InstanceChangedAsync(instance);
            }
        });
    }

    public async Task<P4ServerInstance> StopInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(instanceId, cancellationToken, async (_, instance) =>
        {
            if (runtimeRegistry.Get(instance.Id) != InstanceRuntimeStatus.Running)
            {
                throw new InstanceConflictException($"Instance {instance.Name} is not running.");
            }

            await launcher.StopAsync(instance, cancellationToken);
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.Stopped);
            await notifier.InstanceChangedAsync(instance);
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
                    runtimeRegistry.Set(
                        instance.Id,
                        launcher.GetRunningPid(instance) is null
                            ? InstanceRuntimeStatus.Stopped
                            : InstanceRuntimeStatus.Running);
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
                InstanceRuntimeStatus status = launcher.GetRunningPid(instance) is null
                    ? InstanceRuntimeStatus.Stopped
                    : InstanceRuntimeStatus.Running;
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

    private async Task<P4ServerInstance> RunExclusiveAsync(
        Guid instanceId,
        CancellationToken cancellationToken,
        Func<DaemonDbContext, P4ServerInstance, Task> operation)
    {
        SemaphoreSlim instanceLock = GetLock(instanceId);
        await instanceLock.WaitAsync(cancellationToken);
        try
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await db.Instances
                .Include(i => i.Build)
                .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken)
                ?? throw new KeyNotFoundException($"Instance {instanceId} does not exist.");
            if (instance.State != P4ServerInstanceState.Ready)
            {
                throw new InstanceConflictException($"Instance {instance.Name} is not ready.");
            }

            await operation(db, instance);
            return instance;
        }
        finally
        {
            instanceLock.Release();
        }
    }

    private SemaphoreSlim GetLock(Guid instanceId)
    {
        return instanceLocks.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
    }
}
