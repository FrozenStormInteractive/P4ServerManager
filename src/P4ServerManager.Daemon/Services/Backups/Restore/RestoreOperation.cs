using P4ServerManager.Daemon.Services.Backups.Restore.Steps;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Restore;

/// <summary>
/// Replaces the data of an instance with a backup point: read the point, check that it fits, stop p4d, set the current data aside, rebuild
/// the database from the checkpoint and the journals, put the files where the restored layout says, check the
/// database, start p4d in maintenance mode, verify the archives, then stop p4d and start it again as the instance says
/// if it ran before. The supervisor puts the previous data back if any step fails.
/// </summary>
public sealed class RestoreOperation(
    OpenRestoreSourceStep openSource,
    CheckFreeSpaceStep checkFreeSpace,
    StopServerStep stopServer,
    SetAsideServerFilesStep setAside,
    RestoreMetadataStep restoreMetadata,
    ReplayCheckpointStep replay,
    LocateRestoredFilesStep locate,
    RestoreFilesStep restoreFiles,
    CheckDatabaseStep checkDatabase,
    StartServerInMaintenanceModeStep startInMaintenanceMode,
    VerifyArchivesStep verifyArchives,
    StartServerStep startServer,
    FinishRestoreStep finish) : IOperationDefinition<RestoreContext>
{
    public const string OperationKind = "restore";

    public string Kind => OperationKind;

    public OperationPlan<RestoreContext> Plan(RestoreContext context)
    {
        return new OperationPlan<RestoreContext>()
            .Then(openSource)
            .Then(checkFreeSpace)
            .Then(stopServer, c => !c.WasRunning)
            .Then(setAside)
            .Then(restoreMetadata)
            .Then(replay)
            .Then(locate)
            .Then(restoreFiles)
            .Then(checkDatabase)
            .Then(startInMaintenanceMode)
            .Then(verifyArchives)
            .Then(stopServer)
            .Then(startServer, c => !c.WasRunning)
            .Then(finish);
    }
}
