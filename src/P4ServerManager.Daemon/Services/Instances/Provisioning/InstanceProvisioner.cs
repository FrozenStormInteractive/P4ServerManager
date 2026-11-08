using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
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
    OperationRunner operationRunner,
    P4ServerLauncher launcher,
    InstanceRuntimeRegistry runtimeRegistry,
    InstanceNotifier notifier,
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
}
