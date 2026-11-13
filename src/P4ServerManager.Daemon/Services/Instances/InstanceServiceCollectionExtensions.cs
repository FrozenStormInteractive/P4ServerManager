using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.HealthChecks;
using P4ServerManager.Daemon.Services.Instances.Import;
using P4ServerManager.Daemon.Services.Instances.PortChange;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Instances.UnicodeConversion;
using P4ServerManager.Daemon.Services.Instances.Upgrade;

namespace P4ServerManager.Daemon.Services.Instances;

public static class InstanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything that runs and maintains the instances: their supervision, their operations and the steps of
    /// those operations, and their stored super users. Each operation writes the order of its steps in its plan, so the
    /// order of the registrations does not matter. Needs the operation runner and ASP.NET Core Data Protection.
    /// </summary>
    public static IServiceCollection AddInstanceServices(this IServiceCollection services)
    {
        AddSupervisor(services);
        AddSharedOperationSteps(services);
        AddProvisioning(services);
        AddUpgrade(services);
        AddUnicodeConversion(services);
        AddServerIdAssignment(services);
        AddPortChange(services);
        AddSuperUserManagement(services);
        return services;
    }

    /// <summary>
    /// Registers what starts, stops and watches the p4d processes, and adopts them when the daemon starts.
    /// </summary>
    private static void AddSupervisor(IServiceCollection services)
    {
        services.AddSingleton<InstanceRuntimeRegistry>();
        services.AddSingleton<InstanceNotifier>();
        services.AddSingleton<P4ServerLauncher>();
        services.AddSingleton<ServerConfigurationReader>();
        services.AddSingleton<InstanceSupervisor>();
        services.AddHostedService(provider => provider.GetRequiredService<InstanceSupervisor>());
        services.AddSingleton<InstanceBatchRunner>();
        services.AddSingleton<HealthCheckRunner>();
    }

    /// <summary>
    /// Registers the steps shared by several operations.
    /// </summary>
    private static void AddSharedOperationSteps(IServiceCollection services)
    {
        services.AddSingleton<ResolveBuildStep>();
        services.AddSingleton<StopServerStep>();
        services.AddSingleton<StartServerStep>();
        services.AddSingleton<SaveCheckpointStep>();
        services.AddSingleton<UpgradeDatabaseStep>();
    }

    /// <summary>
    /// Registers the creation and the import of the instances, and their own steps.
    /// </summary>
    private static void AddProvisioning(IServiceCollection services)
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
    }

    /// <summary>
    /// Registers the upgrade of the instances to another p4d build. All its steps are shared.
    /// </summary>
    private static void AddUpgrade(IServiceCollection services)
    {
        services.AddSingleton<UpgradeOperation>();
    }

    /// <summary>
    /// Registers the conversion of the instances to Unicode mode and its own step.
    /// </summary>
    private static void AddUnicodeConversion(IServiceCollection services)
    {
        services.AddSingleton<SwitchToUnicodeStep>();

        services.AddSingleton<UnicodeConversionOperation>();
    }

    /// <summary>
    /// Registers the change of the server ID of the instances and its own steps.
    /// </summary>
    private static void AddServerIdAssignment(IServiceCollection services)
    {
        services.AddSingleton<SetServerIdStep>();
        services.AddSingleton<RenameServerSpecStep>();

        services.AddSingleton<ServerIdAssignmentOperation>();
    }

    /// <summary>
    /// Registers the change of the port of the instances and its own steps.
    /// </summary>
    private static void AddPortChange(IServiceCollection services)
    {
        services.AddSingleton<SetPortStep>();
        services.AddSingleton<UpdateServerAddressStep>();

        services.AddSingleton<PortChangeOperation>();
    }

    /// <summary>
    /// Registers the stored super users of the instances, their periodic check, and the sessions that run p4 commands
    /// as them. Needs ASP.NET Core Data Protection.
    /// </summary>
    private static void AddSuperUserManagement(IServiceCollection services)
    {
        services.AddSingleton<SuperUserPasswordProtector>();
        services.AddSingleton<SuperUserVerifier>();
        services.AddSingleton<SuperUserService>();
        services.AddSingleton<SuperUserMonitor>();
        services.AddHostedService(provider => provider.GetRequiredService<SuperUserMonitor>());
    }
}
