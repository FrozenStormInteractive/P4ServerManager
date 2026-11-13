using P4ServerManager.Backup.Manifests;
using P4ServerManager.Backup.Repository;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Destinations;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Verification;

/// <summary>
/// State of the verification of a backup point at one destination.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/> and <paramref name="record"/>.</param>
/// <param name="instance">The instance.</param>
/// <param name="run">The backup, with its destinations.</param>
/// <param name="record">What the backup did at the destination: the verification is kept there.</param>
/// <param name="destination">The destination.</param>
/// <param name="deep">Whether every object is read back.</param>
public sealed class VerifyBackupContext(
    DaemonDbContext db,
    P4ServerInstance instance,
    BackupRun run,
    BackupRunDestination record,
    BackupDestination destination,
    bool deep) : InstanceOperationContext(db, instance), IOperationOutcome
{
    public const string VerifiedOutcome = "verified";

    public const string ProblemsFoundOutcome = "problems-found";

    public string Outcome => Record.VerificationSucceeded ? VerifiedOutcome : ProblemsFoundOutcome;

    public BackupRun Run { get; } = run;

    public BackupRunDestination Record { get; } = record;

    public BackupDestination Destination { get; } = destination;

    public bool Deep { get; } = deep;
}

/// <summary>
/// Checks a backup point at a destination. p4d is not touched.
/// </summary>
public sealed class VerifyBackupOperation(VerifyBackupStep verify) : IOperationDefinition<VerifyBackupContext>
{
    public const string OperationKind = "verify-backup";

    public string Kind => OperationKind;

    public OperationPlan<VerifyBackupContext> Plan(VerifyBackupContext context)
    {
        return new OperationPlan<VerifyBackupContext>().Then(verify);
    }
}

/// <summary>
/// Reads the manifest of the point and checks its objects: that each one exists, or, for a deep verification, that each
/// one decrypts to the content the manifest tells. The outcome is kept with the backup, whether it found problems or
/// could not run.
/// </summary>
public sealed class VerifyBackupStep(
    BackupDestinationService destinationService,
    BackupRunNotifier runNotifier,
    TimeProvider timeProvider) : IOperationStep<VerifyBackupContext>
{
    /// <summary>
    /// Problems kept with the backup.
    /// </summary>
    public const int KeptProblems = 20;

    public string Key => "verify-backup";

    public string Title => "Verify the backup";

    public async Task<string?> RunAsync(
        VerifyBackupContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        BackupRunDestination record = context.Record;
        string snapshotKey = record.SnapshotKey
            ?? throw new InstanceOperationException("The backup wrote no point at this destination.");
        try
        {
            await using OpenedBackupDestination opened = await destinationService.OpenAsync(context.Destination, cancellationToken);
            BackupManifest manifest = await opened.Repository.ReadManifestAsync(snapshotKey, cancellationToken);
            BackupVerificationResult result = await opened.Repository.VerifyAsync(
                manifest,
                context.Deep,
                new InlineProgress<BackupProgress>(p => progress.Report(new OperationStepProgress(
                    $"{p.Completed} of {p.Total} objects",
                    p.Total == 0 ? 100 : (int)(p.Completed * 100 / p.Total)))),
                cancellationToken);

            record.VerificationSucceeded = result.Succeeded;
            record.VerificationCheckedObjects = result.CheckedObjects;
            record.VerificationProblemCount = result.ProblemCount;
            record.VerificationProblems = result.Problems
                .Take(KeptProblems)
                .Select(p => $"{p.Path}: {p.Message}")
                .ToList();
            record.VerificationError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            record.VerificationSucceeded = false;
            record.VerificationCheckedObjects = 0;
            record.VerificationProblemCount = 0;
            record.VerificationProblems = [];
            record.VerificationError = exception.Message;
            throw new InstanceOperationException($"The backup could not be verified: {exception.Message}", exception);
        }
        finally
        {
            record.VerifiedAt = timeProvider.GetUtcNow();
            record.VerificationDeep = context.Deep;
            await runNotifier.SaveAsync(context.Db, context.Run);
        }

        return record.VerificationSucceeded
            ? $"{record.VerificationCheckedObjects} objects checked, nothing wrong"
            : $"{record.VerificationProblemCount} problems found in {record.VerificationCheckedObjects} objects";
    }
}
