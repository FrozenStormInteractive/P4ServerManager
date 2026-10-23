using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provisioning of the instances. The steps run in the order they are registered here.
    /// </summary>
    public static IServiceCollection AddProvisioning(this IServiceCollection services)
    {
        services.AddSingleton<IOperationStep<ProvisioningContext>, ResolveBuildStep>();
        services.AddSingleton<IOperationStep<ProvisioningContext>, PrepareDirectoriesStep>();
        services.AddSingleton<IOperationStep<ProvisioningContext>, InitializeDatabaseStep>();
        services.AddSingleton<IOperationStep<ProvisioningContext>, StartServerStep>();
        services.AddSingleton<IOperationStep<ProvisioningContext>, CreateSuperUserStep>();

        services.AddSingleton<ProvisioningQueue>();
        services.AddSingleton<InstanceProvisioner>();
        services.AddHostedService<ProvisioningWorker>();
        return services;
    }
}
