using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;

namespace P4ServerManager.Daemon.Services.Instances;

// Change of the server ID of the instances.
public sealed partial class InstanceSupervisor
{
    /// <summary>
    /// Gives an instance another server ID, and renames its server spec as its stored super user. The change runs in the
    /// background, once this returns: the instance stays <see cref="InstanceRuntimeStatus.ChangingServerId"/> until it
    /// ends, and its operation tells its progress. p4d is stopped if it runs, started to rename the spec, then stopped
    /// again if it did not run. Only the shutdown of the daemon cancels it, and never while p4d sets the server ID.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="serverId">New server ID, well-formed.</param>
    /// <param name="cancellationToken">Aborts the wait for the instance to be free.</param>
    /// <returns>The instance, once the change has begun.</returns>
    /// <exception cref="InstanceConflictException">
    /// The instance is not ready, another operation is in progress, it already has this server ID, or no usable super
    /// user is stored for it.
    /// </exception>
    public async Task<P4ServerInstance> ChangeServerIdAsync(
        Guid instanceId,
        string serverId,
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
            // Read from the root, as shown: the change itself asks p4d once it is stopped.
            if (await ServerLayout.TryReadServerIdAsync(instance.RootPath, cancellationToken) == serverId)
            {
                throw new InstanceConflictException($"Instance {instance.Name} already has server ID {serverId}.");
            }

            SuperUserCredentials superUser;
            try
            {
                superUser = superUserService.GetStoredCredentials(instance);
            }
            catch (SuperUserUnavailableException exception)
            {
                throw new InstanceConflictException(
                    $"{exception.Message} The server spec is renamed as the super user.");
            }

            ServerIdAssignmentContext context = new(
                db,
                instance,
                launcher.IsRunning(instance),
                serverId,
                superUser);
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.ChangingServerId);
            await notifier.InstanceChangedAsync(instance);

            // The change outlives the request: it releases the instance and the context once it ends.
            _ = RunInBackgroundAsync(
                db,
                instance,
                instanceLock,
                stoppingToken => AssignServerIdAsync(context, stoppingToken));
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
    /// Runs the change. A failure is kept as the last error of the instance, which is not saved: the caller saves it.
    /// p4d is then left as it was before the change: started again if it ran, stopped again if it did not. The server ID
    /// stays the one the root has.
    /// </summary>
    private async Task AssignServerIdAsync(ServerIdAssignmentContext context, CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        Exception? failure = await RunOperationAsync(
            serverIdAssignmentOperation,
            context,
            () => logger.LogInformation(
                "Changing the server ID of instance {Name} to {ServerId}", instance.Name, context.NewServerId),
            cancellationToken);
        if (failure is null)
        {
            instance.LastError = null;
            logger.LogInformation(
                "Changed the server ID of instance {Name} to {ServerId}", instance.Name, context.NewServerId);
            return;
        }

        logger.LogError(failure, "Change of the server ID of instance {Name} failed", instance.Name);
        instance.LastError = failure.Message;
        await RestoreRunningStateAsync(instance, context.WasRunning, cancellationToken);
    }
}
