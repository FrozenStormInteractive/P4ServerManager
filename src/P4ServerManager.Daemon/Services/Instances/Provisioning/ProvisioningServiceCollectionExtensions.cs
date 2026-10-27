using P4ServerManager.Daemon.Services.Instances.Import;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provisioning of the instances. The steps of each <see cref="ProvisioningKind"/> run in the order
    /// they are registered here.
    /// </summary>
    public static IServiceCollection AddProvisioning(this IServiceCollection services)
    {
        services.AddStep<ResolveBuildStep>(ProvisioningKind.Create);
        services.AddStep<PrepareDirectoriesStep>(ProvisioningKind.Create);
        services.AddStep<InitializeDatabaseStep>(ProvisioningKind.Create);
        services.AddStep<GenerateSslCertificateStep>(ProvisioningKind.Create);
        services.AddStep<StartServerStep>(ProvisioningKind.Create);
        services.AddStep<CreateSuperUserStep>(ProvisioningKind.Create);
        services.AddStep<ConfigureServerStep>(ProvisioningKind.Create);
        services.AddStep<CreateSpecsStep>(ProvisioningKind.Create);

        services.AddStep<ResolveBuildStep>(ProvisioningKind.Import);
        services.AddStep<CheckRootStep>(ProvisioningKind.Import);
        services.AddStep<SaveCheckpointStep>(ProvisioningKind.Import);
        services.AddStep<ReadServerSettingsStep>(ProvisioningKind.Import);
        services.AddStep<UpgradeDatabaseStep>(ProvisioningKind.Import);
        services.AddStep<StartServerStep>(ProvisioningKind.Import);

        services.AddSingleton<ProvisioningQueue>();
        services.AddSingleton<InstanceProvisioner>();
        services.AddSingleton<LegacyProfileStore>();
        services.AddHostedService<ProvisioningWorker>();
        return services;
    }

    private static void AddStep<TStep>(this IServiceCollection services, ProvisioningKind kind)
        where TStep : class, IOperationStep<ProvisioningContext>
    {
        services.AddKeyedSingleton<IOperationStep<ProvisioningContext>, TStep>(kind);
    }
}
