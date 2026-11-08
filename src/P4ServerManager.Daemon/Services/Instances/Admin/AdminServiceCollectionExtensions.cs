namespace P4ServerManager.Daemon.Services.Instances.Admin;

public static class AdminServiceCollectionExtensions
{
    /// <summary>
    /// Registers the stored super users of the instances, their periodic check, and the sessions that run p4 commands
    /// as them. Needs ASP.NET Core Data Protection.
    /// </summary>
    public static IServiceCollection AddInstanceAdminServices(this IServiceCollection services)
    {
        services.AddSingleton<SuperUserPasswordProtector>();
        services.AddSingleton<SuperUserVerifier>();
        services.AddSingleton<SuperUserService>();
        services.AddSingleton<SuperUserMonitor>();
        services.AddHostedService(provider => provider.GetRequiredService<SuperUserMonitor>());
        return services;
    }
}
