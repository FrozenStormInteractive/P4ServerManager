using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

public static class UpgradeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the upgrade of the instances to another p4d build. The steps run in the order they are registered here.
    /// </summary>
    public static IServiceCollection AddInstanceUpgradeServices(this IServiceCollection services)
    {
        services.AddStep<DownloadTargetBuildStep>();
        services.AddStep<StopServerStep>();
        services.AddStep<UpgradeDatabaseToTargetStep>();
        services.AddStep<StartUpgradedServerStep>();

        services.AddSingleton<InstanceUpgrader>();
        return services;
    }

    private static void AddStep<TStep>(this IServiceCollection services)
        where TStep : class, IOperationStep<UpgradeContext>
    {
        services.AddSingleton<IOperationStep<UpgradeContext>, TStep>();
    }
}
