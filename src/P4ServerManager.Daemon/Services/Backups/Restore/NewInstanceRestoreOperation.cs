using P4ServerManager.Daemon.Services.Backups.Restore.Steps;
using P4ServerManager.Daemon.Services.Instances.PortChange;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Restore;

/// <summary>
/// Makes a new instance from a backup point: read the point, check that it fits, get the p4d build, prepare the folders, rebuild the
/// database from the checkpoint and the journals, give it the server ID and the layout of the instance, put the files
/// back, check the database, start p4d in maintenance mode, relocate the depots kept elsewhere, name the server spec after the instance and
/// point its address to the new port, verify the archives, then stop p4d and start it again as the instance says. A failure leaves the instance failed with its folders as
/// they are, as a creation does.
/// </summary>
public sealed class NewInstanceRestoreOperation(
    OpenRestoreSourceStep openSource,
    CheckFreeSpaceStep checkFreeSpace,
    ResolveBuildStep resolveBuild,
    PrepareDirectoriesStep prepareDirectories,
    RestoreMetadataStep restoreMetadata,
    ReplayCheckpointStep replay,
    ConfigureRestoredServerStep configure,
    LocateRestoredFilesStep locate,
    RestoreFilesStep restoreFiles,
    ReadServerSettingsStep readSettings,
    CheckDatabaseStep checkDatabase,
    RestoreSslStep restoreSsl,
    StartServerInMaintenanceModeStep startInMaintenanceMode,
    RelocateDepotsStep relocateDepots,
    RenameServerSpecStep renameServerSpec,
    UpdateServerAddressStep updateServerAddress,
    VerifyArchivesStep verifyArchives,
    StopServerStep stopServer,
    StartServerStep startServer,
    FinishRestoreStep finish) : IOperationDefinition<NewInstanceRestoreContext>
{
    public const string OperationKind = "restore-instance";

    public string Kind => OperationKind;

    public OperationPlan<NewInstanceRestoreContext> Plan(NewInstanceRestoreContext context)
    {
        return new OperationPlan<NewInstanceRestoreContext>()
            .Then(openSource)
            .Then(checkFreeSpace)
            .Then(resolveBuild)
            .Then(prepareDirectories)
            .Then(restoreMetadata)
            .Then(replay)
            .Then(configure)
            .Then(locate)
            .Then(restoreFiles)
            .Then(readSettings)
            .Then(checkDatabase)
            .Then(restoreSsl)
            .Then(startInMaintenanceMode)
            .Then(relocateDepots)
            .Then(renameServerSpec)
            .Then(updateServerAddress)
            .Then(verifyArchives)
            .Then(stopServer)
            .Then(startServer)
            .Then(finish);
    }
}
