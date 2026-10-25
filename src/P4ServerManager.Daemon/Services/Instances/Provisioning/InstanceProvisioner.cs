using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Turns a new instance into a running p4d by running the steps of its <see cref="ProvisioningKind"/>, in the order
/// they were registered by <see cref="ProvisioningServiceCollectionExtensions.AddProvisioning"/>.
/// </summary>
public sealed class InstanceProvisioner(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    [FromKeyedServices(ProvisioningKind.Create)] IEnumerable<IOperationStep<ProvisioningContext>> createSteps,
    [FromKeyedServices(ProvisioningKind.Import)] IEnumerable<IOperationStep<ProvisioningContext>> importSteps,
    OperationRunner operationRunner,
    P4ServerLauncher launcher,
    InstanceRuntimeRegistry runtimeRegistry,
    InstanceNotifier notifier,
    ILogger<InstanceProvisioner> logger)
{
    public const string OperationKind = "provision";

    public const string ImportOperationKind = "import";

    private readonly IReadOnlyList<IOperationStep<ProvisioningContext>> provisioningSteps = createSteps.ToList();

    private readonly IReadOnlyList<IOperationStep<ProvisioningContext>> importingSteps = importSteps.ToList();

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
        ProvisioningContext context = new(db, instance, request.SuperUser);
        (string kind, IReadOnlyList<IOperationStep<ProvisioningContext>> steps) = request.Kind switch
        {
            ProvisioningKind.Import => (ImportOperationKind, importingSteps),
            _ => (OperationKind, provisioningSteps),
        };
        try
        {
            await operationRunner.RunAsync(instance.Id, kind, steps, context, cancellationToken);

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

            if (context.ServerStarted)
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
}
