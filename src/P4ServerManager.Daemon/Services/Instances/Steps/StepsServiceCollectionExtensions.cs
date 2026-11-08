namespace P4ServerManager.Daemon.Services.Instances.Steps;

public static class StepsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the steps shared by several operations on an instance. Each operation registers its own steps, and
    /// writes the order of all of them in its plan.
    /// </summary>
    public static IServiceCollection AddSharedInstanceSteps(this IServiceCollection services)
    {
        services.AddSingleton<ResolveBuildStep>();
        services.AddSingleton<StopServerStep>();
        services.AddSingleton<StartServerStep>();
        services.AddSingleton<SaveCheckpointStep>();
        services.AddSingleton<UpgradeDatabaseStep>();
        return services;
    }
}
