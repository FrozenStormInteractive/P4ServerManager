namespace P4ServerManager.Daemon.Services.Instances.UnicodeConversion;

public static class UnicodeConversionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the conversion of the instances to Unicode mode and its own steps. The steps it shares with other
    /// operations are registered by <see cref="Steps.StepsServiceCollectionExtensions.AddSharedInstanceSteps"/>.
    /// </summary>
    public static IServiceCollection AddInstanceUnicodeConversionServices(this IServiceCollection services)
    {
        services.AddSingleton<SwitchToUnicodeStep>();

        services.AddSingleton<UnicodeConversionOperation>();
        services.AddSingleton<InstanceUnicodeConverter>();
        return services;
    }
}
