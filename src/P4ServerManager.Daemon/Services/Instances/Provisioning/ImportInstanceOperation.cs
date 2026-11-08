using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Takes over an existing root as it is: a checkpoint is saved before its database is upgraded to the build of the
/// instance.
/// </summary>
public sealed class ImportInstanceOperation(
    ResolveBuildStep resolveBuild,
    CheckRootStep checkRoot,
    ReadSslFingerprintStep readSslFingerprint,
    SaveCheckpointStep saveCheckpoint,
    ReadServerSettingsStep readServerSettings,
    UpgradeDatabaseStep upgradeDatabase,
    StartServerStep startServer) : IOperationDefinition<ImportInstanceContext>
{
    public const string OperationKind = "import";

    public string Kind => OperationKind;

    public OperationPlan<ImportInstanceContext> Plan(ImportInstanceContext context)
    {
        return new OperationPlan<ImportInstanceContext>()
            .Then(resolveBuild)
            .Then(checkRoot)
            .ThenIf(context.Instance.Ssl, readSslFingerprint)
            .Then(saveCheckpoint)
            .Then(readServerSettings)
            .Then(upgradeDatabase)
            .Then(startServer);
    }
}
