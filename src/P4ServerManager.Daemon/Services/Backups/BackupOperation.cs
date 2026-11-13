using P4ServerManager.Daemon.Services.Backups.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups;

/// <summary>
/// Backs up an instance while p4d keeps running: open the destinations, freeze the database with a checkpoint or a
/// rotation of the journal, list the files, upload them to each destination, then keep the index of the files.
/// </summary>
public sealed class BackupOperation(
    OpenBackupDestinationsStep openDestinations,
    SaveBackupMetadataStep saveMetadata,
    ScanServerFilesStep scanServerFiles,
    UploadBackupStepFactory uploadSteps,
    FinishBackupStep finish) : IOperationDefinition<BackupContext>
{
    public const string OperationKind = "backup";

    public string Kind => OperationKind;

    public OperationPlan<BackupContext> Plan(BackupContext context)
    {
        OperationPlan<BackupContext> plan = new OperationPlan<BackupContext>()
            .Then(openDestinations)
            .Then(saveMetadata)
            .Then(scanServerFiles);
        for (int i = 0; i < context.Destinations.Count; i++)
        {
            plan.Then(uploadSteps.Create(i, context.Destinations[i].Destination.Name));
        }
        return plan.Then(finish);
    }
}
