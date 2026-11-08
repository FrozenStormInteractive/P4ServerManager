namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

public static class UpgradeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the upgrade of the instances to another p4d build. All its steps are shared with other operations,
    /// and registered by <see cref="Steps.StepsServiceCollectionExtensions.AddSharedInstanceSteps"/>.
    /// </summary>
    public static IServiceCollection AddInstanceUpgradeServices(this IServiceCollection services)
    {
        services.AddSingleton<UpgradeOperation>();
        services.AddSingleton<InstanceUpgrader>();
        return services;
    }
}
