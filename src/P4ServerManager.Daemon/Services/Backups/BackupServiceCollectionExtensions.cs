using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Backups.Destinations;
using P4ServerManager.Daemon.Services.Backups.Steps;
using P4ServerManager.Daemon.Services.Backups.Verification;

namespace P4ServerManager.Daemon.Services.Backups;

public static class BackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the backup destinations, the storage targets they write to, and the backup and verification
    /// operations. Needs ASP.NET Core Data Protection.
    /// </summary>
    public static IServiceCollection AddBackupServices(this IServiceCollection services)
    {
        services.AddSingleton<BackupSecretProtector>();
        services.AddSingleton<BackupTargetFactory>();
        services.AddSingleton<IBackupTargetProvider, LocalBackupTargetProvider>();
        services.AddSingleton<IBackupTargetProvider, S3BackupTargetProvider>();
        services.AddSingleton<IBackupTargetProvider, GcsBackupTargetProvider>();
        services.AddSingleton<IBackupTargetProvider, AzureBlobBackupTargetProvider>();
        services.AddSingleton<IBackupTargetProvider, FtpBackupTargetProvider>();
        services.AddSingleton<BackupDestinationService>();
        services.AddSingleton<BackupRunNotifier>();
        services.AddSingleton<ServerFileLocator>();

        services.AddSingleton<OpenBackupDestinationsStep>();
        services.AddSingleton<SaveBackupMetadataStep>();
        services.AddSingleton<ScanServerFilesStep>();
        services.AddSingleton<UploadBackupStepFactory>();
        services.AddSingleton<FinishBackupStep>();
        services.AddSingleton<BackupOperation>();

        services.AddSingleton<VerifyBackupStep>();
        services.AddSingleton<VerifyBackupOperation>();
        return services;
    }
}
