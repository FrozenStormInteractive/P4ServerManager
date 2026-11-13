using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Backups.Destinations;
using P4ServerManager.Daemon.Services.Backups.Restore;
using P4ServerManager.Daemon.Services.Backups.Restore.Steps;
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

        services.AddSingleton<BackupPointService>();
        services.AddScoped<RestoredInstanceService>();
        services.AddSingleton<RestoreRunNotifier>();
        services.AddSingleton<IVolumeSpaceReader, VolumeSpaceReader>();
        services.AddSingleton<OpenRestoreSourceStep>();
        services.AddSingleton<CheckFreeSpaceStep>();
        services.AddSingleton<SetAsideServerFilesStep>();
        services.AddSingleton<RestoreMetadataStep>();
        services.AddSingleton<ReplayCheckpointStep>();
        services.AddSingleton<LocateRestoredFilesStep>();
        services.AddSingleton<RestoreFilesStep>();
        services.AddSingleton<CheckDatabaseStep>();
        services.AddSingleton<VerifyArchivesStep>();
        services.AddSingleton<FinishRestoreStep>();
        services.AddSingleton<RestoreOperation>();
        services.AddSingleton<ConfigureRestoredServerStep>();
        services.AddSingleton<RestoreSslStep>();
        services.AddSingleton<RelocateDepotsStep>();
        services.AddSingleton<NewInstanceRestoreOperation>();
        return services;
    }
}
