using System.Runtime.CompilerServices;

using P4ServerManager.Daemon.Services.Backups.Restore;
using P4ServerManager.Daemon.Services.Backups.Restore.Steps;
using P4ServerManager.Daemon.Services.Instances.PortChange;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Tests.Services.Backups;

/// <summary>
/// The plans of the restores: p4d runs in maintenance mode while the restore needs it, then starts as the instance
/// says. Building a plan runs no step: the steps get no services.
/// </summary>
public sealed class RestoreOperationPlanTests
{
    [Fact]
    public void Restore_VerifiesInMaintenanceModeThenStartsAsTheInstanceSays()
    {
        RestoreOperation operation = new(
            Create<OpenRestoreSourceStep>(),
            Create<CheckFreeSpaceStep>(),
            Create<StopServerStep>(),
            Create<SetAsideServerFilesStep>(),
            Create<RestoreMetadataStep>(),
            Create<ReplayCheckpointStep>(),
            Create<LocateRestoredFilesStep>(),
            Create<RestoreFilesStep>(),
            Create<CheckDatabaseStep>(),
            Create<StartServerInMaintenanceModeStep>(),
            Create<VerifyArchivesStep>(),
            Create<StartServerStep>(),
            Create<FinishRestoreStep>());

        OperationPlan<RestoreContext> plan = operation.Plan(null!);

        plan.Steps.Select(s => s.Step.Key).SkipWhile(k => k != "check-database").ShouldBe(
            ["check-database", "start-server-maintenance", "verify-archives", "stop-server", "start-server", "finish-restore"]);
    }

    [Fact]
    public void NewInstance_RunsEveryServerStepInMaintenanceModeThenStartsAsTheInstanceSays()
    {
        NewInstanceRestoreOperation operation = new(
            Create<OpenRestoreSourceStep>(),
            Create<CheckFreeSpaceStep>(),
            Create<ResolveBuildStep>(),
            Create<PrepareDirectoriesStep>(),
            Create<RestoreMetadataStep>(),
            Create<ReplayCheckpointStep>(),
            Create<ConfigureRestoredServerStep>(),
            Create<LocateRestoredFilesStep>(),
            Create<RestoreFilesStep>(),
            Create<ReadServerSettingsStep>(),
            Create<CheckDatabaseStep>(),
            Create<RestoreSslStep>(),
            Create<StartServerInMaintenanceModeStep>(),
            Create<RelocateDepotsStep>(),
            Create<RenameServerSpecStep>(),
            Create<UpdateServerAddressStep>(),
            Create<VerifyArchivesStep>(),
            Create<StopServerStep>(),
            Create<StartServerStep>(),
            Create<FinishRestoreStep>());

        OperationPlan<NewInstanceRestoreContext> plan = operation.Plan(null!);

        plan.Steps.Select(s => s.Step.Key).SkipWhile(k => k != "start-server-maintenance").ShouldBe(
        [
            "start-server-maintenance", "relocate-depots", "rename-server-spec", "update-server-address",
            "verify-archives", "stop-server", "start-server", "finish-restore",
        ]);
    }

    private static T Create<T>()
        where T : class
    {
        return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }
}
