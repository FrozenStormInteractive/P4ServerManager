using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Restore;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Turns a new instance into a running p4d with the operation of its <see cref="ProvisioningKind"/>: the
/// <see cref="CreateInstanceOperation"/> or the <see cref="ImportInstanceOperation"/>.
/// </summary>
public sealed class InstanceProvisioner(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    CreateInstanceOperation createOperation,
    ImportInstanceOperation importOperation,
    NewInstanceRestoreOperation restoreOperation,
    RestoreRunNotifier restoreRunNotifier,
    TimeProvider timeProvider,
    OperationRunner operationRunner,
    P4ServerLauncher launcher,
    InstanceRuntimeRegistry runtimeRegistry,
    InstanceNotifier notifier,
    SuperUserMonitor superUserMonitor,
    DaemonPaths paths,
    ILogger<InstanceProvisioner> logger)
{
    public async Task ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        P4ServerInstance? instance = await db.Instances
            .Include(i => i.Build)
            .FirstOrDefaultAsync(i => i.Id == request.InstanceId, cancellationToken);
        if (instance is null)
        {
            return;
        }

        logger.LogInformation("Provisioning instance {Name} ({Kind})", instance.Name, request.Kind);
        try
        {
            await RunOperationAsync(request, db, instance, cancellationToken);

            instance.State = P4ServerInstanceState.Ready;
            instance.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            await notifier.InstanceChangedAsync(instance);
            logger.LogInformation("Instance {Name} is ready", instance.Name);

            // Its server started while it was not ready: its stored super user is checked now.
            superUserMonitor.RequestCheck(instance.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Provisioning of instance {Name} failed", instance.Name);

            if (launcher.IsRunning(instance))
            {
                try
                {
                    await launcher.StopAsync(instance, CancellationToken.None);
                }
                catch (Exception stopException)
                {
                    logger.LogWarning(stopException, "Could not stop p4d of instance {Name}", instance.Name);
                }
            }

            // The root is left as is, for diagnosis.
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.Stopped);
            instance.State = P4ServerInstanceState.Failed;
            instance.LastError = exception.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            await notifier.InstanceChangedAsync(instance);
        }
    }

    private Task RunOperationAsync(
        ProvisioningRequest request,
        DaemonDbContext db,
        P4ServerInstance instance,
        CancellationToken cancellationToken)
    {
        if (request.Kind == ProvisioningKind.Restore)
        {
            return RestoreAsync(request, db, instance, cancellationToken);
        }
        if (request.Kind == ProvisioningKind.Import)
        {
            ImportInstanceContext context = new(db, instance, paths.GetImportCheckpointPath(instance.Id));
            return operationRunner.RunAsync(instance.Id, importOperation, context, cancellationToken);
        }

        SuperUserCredentials superUser = request.SuperUser
            ?? throw new InvalidOperationException("A created server needs a super user.");
        return operationRunner.RunAsync(
            instance.Id,
            createOperation,
            new CreateInstanceContext(db, instance, superUser),
            cancellationToken);
    }

    /// <summary>
    /// Runs the restore of a point into the new instance, and keeps how it ended with the run, whatever happens.
    /// </summary>
    private async Task RestoreAsync(
        ProvisioningRequest request,
        DaemonDbContext db,
        P4ServerInstance instance,
        CancellationToken cancellationToken)
    {
        RestoreProvisioning restore = request.Restore
            ?? throw new InvalidOperationException("A restored server needs its restore.");
        SuperUserCredentials superUser = request.SuperUser
            ?? throw new InvalidOperationException("A restored server needs a super user.");
        RestoreRun run = await db.RestoreRuns.FirstOrDefaultAsync(r => r.Id == restore.RunId, cancellationToken)
            ?? throw new InvalidOperationException($"Restore {restore.RunId} does not exist.");

        NewInstanceRestoreContext? context = null;
        try
        {
            BackupDestination destination = await db.BackupDestinations
                .FirstOrDefaultAsync(d => d.Id == run.DestinationId, cancellationToken)
                ?? throw new InstanceOperationException($"Backup destination {run.DestinationName} was removed from the daemon.");
            context = new NewInstanceRestoreContext(db, instance, run, destination, superUser)
            {
                SuperUser = superUser,
                RestoreLicense = restore.RestoreLicense,
            };
            await operationRunner.RunAsync(instance.Id, restoreOperation, context, cancellationToken);
            run.Status = context.Outcome == RestoreContext.RestoredOutcome
                ? RestoreRunStatus.Succeeded
                : RestoreRunStatus.SucceededWithProblems;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Status = RestoreRunStatus.Interrupted;
            run.Error = "The daemon stopped during the restore.";
            throw;
        }
        catch (Exception exception)
        {
            run.Status = RestoreRunStatus.Failed;
            run.Error = exception.Message;
            throw;
        }
        finally
        {
            run.FinishedAt = timeProvider.GetUtcNow();
            if (context is not null)
            {
                await context.CloseAsync();
            }
            await restoreRunNotifier.SaveAsync(db, run);
        }
    }
}
