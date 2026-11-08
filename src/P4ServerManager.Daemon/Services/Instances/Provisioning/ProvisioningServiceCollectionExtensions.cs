using P4ServerManager.Daemon.Services.Instances.Import;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provisioning of the instances and its own steps. The steps it shares with other operations are
    /// registered by <see cref="Steps.StepsServiceCollectionExtensions.AddSharedInstanceSteps"/>.
    /// </summary>
    public static IServiceCollection AddInstanceProvisioningServices(this IServiceCollection services)
    {
        services.AddSingleton<PrepareDirectoriesStep>();
        services.AddSingleton<InitializeDatabaseStep>();
        services.AddSingleton<GenerateSslCertificateStep>();
        services.AddSingleton<CreateSuperUserStep>();
        services.AddSingleton<ConfigureServerStep>();
        services.AddSingleton<CreateSpecsStep>();
        services.AddSingleton<CheckRootStep>();
        services.AddSingleton<ReadSslFingerprintStep>();
        services.AddSingleton<ReadServerSettingsStep>();

        services.AddSingleton<CreateInstanceOperation>();
        services.AddSingleton<ImportInstanceOperation>();
        services.AddSingleton<ProvisioningQueue>();
        services.AddSingleton<InstanceProvisioner>();
        services.AddSingleton<LegacyProfileStore>();
        services.AddHostedService<ProvisioningWorker>();
        return services;
    }
}
