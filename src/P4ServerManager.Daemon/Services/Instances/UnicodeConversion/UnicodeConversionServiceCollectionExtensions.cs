using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.UnicodeConversion;

public static class UnicodeConversionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the conversion of the instances to Unicode mode. The steps run in the order they are registered here.
    /// </summary>
    public static IServiceCollection AddInstanceUnicodeConversionServices(this IServiceCollection services)
    {
        services.AddStep<StopServerForConversionStep>();
        services.AddStep<SaveConversionCheckpointStep>();
        services.AddStep<SwitchToUnicodeStep>();
        services.AddStep<StartConvertedServerStep>();

        services.AddSingleton<InstanceUnicodeConverter>();
        return services;
    }

    private static void AddStep<TStep>(this IServiceCollection services)
        where TStep : class, IOperationStep<UnicodeConversionContext>
    {
        services.AddSingleton<IOperationStep<UnicodeConversionContext>, TStep>();
    }
}
