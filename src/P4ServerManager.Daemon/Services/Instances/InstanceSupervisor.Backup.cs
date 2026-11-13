using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups;
using P4ServerManager.Daemon.Services.Backups.Verification;

namespace P4ServerManager.Daemon.Services.Instances;

// Backups of the instances, and their verification.
public sealed partial class InstanceSupervisor
{
    // The backup in progress on each instance, so that it can be canceled.
    private readonly ConcurrentDictionary<Guid, (Guid RunId, CancellationTokenSource Cancellation)> backupCancellations = new();

    /// <summary>
    /// Begins a backup of an instance to its destinations, or to some of them. It runs in the background once this
    /// returns, while p4d keeps running: the instance stays <see cref="InstanceRuntimeStatus.BackingUp"/> until it
    /// ends, and its operation tells its progress. It is the single entry point of the backups, whatever began them.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="kind">Full or incremental.</param>
    /// <param name="destinationIds">Destinations of the instance to back up to, or null or empty for all of them.</param>
    /// <param name="trigger">What began the backup.</param>
    /// <param name="cancellationToken">Aborts the wait for the instance to be free.</param>
    /// <returns>The run, saved as running.</returns>
    /// <exception cref="InstanceConflictException">
    /// The instance is not ready, another operation is in progress, it has no backup destination, or one of
    /// <paramref name="destinationIds"/> is not one of its destinations.
    /// </exception>
    public async Task<BackupRun> StartBackupAsync(
        Guid instanceId,
        BackupKind kind,
        IReadOnlyList<Guid>? destinationIds,
        BackupTrigger trigger,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim? instanceLock = await AcquireLockAsync(instanceId, cancellationToken);
        DaemonDbContext? db = null;
        try
        {
            db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await FindAsync(db, instanceId, cancellationToken);
            EnsureReady(instance);
            if (instance.Build is null)
            {
                throw new InstanceConflictException($"Instance {instance.Name} has no p4d build yet.");
            }

            List<BackupDestination> destinations = await db.InstanceBackupDestinations
                .Where(i => i.InstanceId == instanceId)
                .Select(i => i.Destination!)
                .ToListAsync(cancellationToken);
            if (destinations.Count == 0)
            {
                throw new InstanceConflictException($"Instance {instance.Name} has no backup destination.");
            }
            if (destinationIds is { Count: > 0 })
            {
                if (destinationIds.Any(id => destinations.All(d => d.Id != id)))
                {
                    throw new InstanceConflictException(
                        $"A destination asked for is not a backup destination of instance {instance.Name}.");
                }
                destinations = destinations.Where(d => destinationIds.Contains(d.Id)).ToList();
            }
            destinations = destinations.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();

            BackupRun run = new()
            {
                Id = Guid.NewGuid(),
                InstanceId = instance.Id,
                InstanceName = instance.Name,
                Kind = kind,
                Trigger = trigger,
                Status = BackupRunStatus.Running,
                StartedAt = timeProvider.GetUtcNow(),
                Destinations = destinations
                    .Select(d => new BackupRunDestination
                    {
                        DestinationId = d.Id,
                        DestinationName = d.Name,
                        Status = BackupDestinationStatus.Pending,
                    })
                    .ToList(),
            };
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);

            BackupContext context = new(db, instance, run, destinations)
            {
                ServerId = await ServerLayout.TryReadServerIdAsync(instance.RootPath, cancellationToken),
            };
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            backupCancellations[instance.Id] = (run.Id, cancellation);
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.BackingUp);
            await notifier.InstanceChangedAsync(instance);
            await backupRunNotifier.SaveAsync(db, run);

            // The backup outlives the request: it releases the instance and the context once it ends.
            _ = RunInBackgroundAsync(db, instance, instanceLock, _ => BackupAsync(context, cancellation));
            instanceLock = null;
            db = null;
            return run;
        }
        finally
        {
            if (db is not null)
            {
                await db.DisposeAsync();
            }
            instanceLock?.Release();
        }
    }

    /// <summary>
    /// Asks a running backup to stop. It ends shortly after, as <see cref="BackupRunStatus.Canceled"/>: the
    /// destinations it had not finished keep no point.
    /// </summary>
    /// <returns>Whether the backup was still running.</returns>
    public async Task<bool> CancelBackupAsync(Guid instanceId, Guid runId)
    {
        if (!backupCancellations.TryGetValue(instanceId, out (Guid RunId, CancellationTokenSource Cancellation) active)
            || active.RunId != runId)
        {
            return false;
        }

        try
        {
            await active.Cancellation.CancelAsync();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // It ended in the meantime.
            return false;
        }
    }

    /// <summary>
    /// Begins the verification of a backup point at one of its destinations. It runs in the background once this
    /// returns: the instance stays <see cref="InstanceRuntimeStatus.VerifyingBackup"/> until it ends. p4d is not touched.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The instance or the backup does not exist.</exception>
    /// <exception cref="InstanceConflictException">
    /// The instance is not ready, another operation is in progress, or the backup wrote no point at the destination.
    /// </exception>
    public async Task<BackupRun> StartBackupVerificationAsync(
        Guid instanceId,
        Guid runId,
        Guid destinationId,
        bool deep,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim? instanceLock = await AcquireLockAsync(instanceId, cancellationToken);
        DaemonDbContext? db = null;
        try
        {
            db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await FindAsync(db, instanceId, cancellationToken);
            EnsureReady(instance);
            BackupRun run = await db.BackupRuns
                .Include(r => r.Destinations)
                .FirstOrDefaultAsync(r => r.Id == runId && r.InstanceId == instanceId, cancellationToken)
                ?? throw new KeyNotFoundException($"Backup {runId} of instance {instance.Name} does not exist.");
            BackupRunDestination record = run.Destinations.FirstOrDefault(d => d.DestinationId == destinationId)
                ?? throw new InstanceConflictException("The backup did not go to this destination.");
            if (record.SnapshotKey is null)
            {
                throw new InstanceConflictException($"The backup wrote no point to {record.DestinationName}.");
            }
            BackupDestination destination = await db.BackupDestinations
                .FirstOrDefaultAsync(d => d.Id == destinationId, cancellationToken)
                ?? throw new InstanceConflictException(
                    $"Backup destination {record.DestinationName} was removed from the daemon.");

            VerifyBackupContext context = new(db, instance, run, record, destination, deep);
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.VerifyingBackup);
            await notifier.InstanceChangedAsync(instance);

            _ = RunInBackgroundAsync(db, instance, instanceLock, stoppingToken => VerifyBackupAsync(context, stoppingToken));
            instanceLock = null;
            db = null;
            return run;
        }
        finally
        {
            if (db is not null)
            {
                await db.DisposeAsync();
            }
            instanceLock?.Release();
        }
    }

    /// <summary>
    /// Runs a backup and keeps how it ended with the run, whatever happens.
    /// </summary>
    private async Task BackupAsync(BackupContext context, CancellationTokenSource cancellation)
    {
        P4ServerInstance instance = context.Instance;
        BackupRun run = context.Run;
        try
        {
            Exception? failure = await RunOperationAsync(
                backupOperation,
                context,
                () => logger.LogInformation(
                    "Backing up instance {Name} ({Kind}) to {Count} destinations",
                    instance.Name,
                    run.Kind,
                    context.Destinations.Count),
                cancellation.Token);
            if (failure is null)
            {
                run.Status = context.Outcome == BackupContext.CompletedOutcome
                    ? BackupRunStatus.Succeeded
                    : BackupRunStatus.PartiallySucceeded;
                logger.LogInformation("Backed up instance {Name}: {Status}", instance.Name, run.Status);
            }
            else
            {
                logger.LogError(failure, "Backup of instance {Name} failed", instance.Name);
                run.Status = BackupRunStatus.Failed;
                run.Error = failure.Message;
            }
        }
        catch (OperationCanceledException)
        {
            bool stopping = lifetime.ApplicationStopping.IsCancellationRequested;
            run.Status = stopping ? BackupRunStatus.Interrupted : BackupRunStatus.Canceled;
            run.Error = stopping ? "The daemon stopped during the backup." : "The backup was canceled.";
            logger.LogWarning("Backup of instance {Name} {Outcome}", instance.Name, stopping ? "interrupted" : "canceled");
            if (!stopping)
            {
                try
                {
                    await notifier.OperationChangedAsync(operationTracker.Fail(instance.Id, run.Error));
                }
                catch (InvalidOperationException)
                {
                    // The operation had not begun.
                }
            }
        }
        finally
        {
            foreach (BackupRunDestination destination in run.Destinations)
            {
                if (destination.Status is BackupDestinationStatus.Pending or BackupDestinationStatus.Uploading)
                {
                    destination.Status = BackupDestinationStatus.Failed;
                    destination.Error ??= run.Error ?? "The backup ended before this destination.";
                }
            }
            run.FinishedAt = timeProvider.GetUtcNow();
            await context.CloseDestinationsAsync();
            backupCancellations.TryRemove(instance.Id, out _);
            cancellation.Dispose();
            await backupRunNotifier.SaveAsync(context.Db, run);
        }
    }

    private async Task VerifyBackupAsync(VerifyBackupContext context, CancellationToken cancellationToken)
    {
        Exception? failure = await RunOperationAsync(
            verifyBackupOperation,
            context,
            () => logger.LogInformation(
                "Verifying backup {Run} of instance {Name} at {Destination}",
                context.Run.Id,
                context.Instance.Name,
                context.Destination.Name),
            cancellationToken);
        if (failure is not null)
        {
            logger.LogError(failure, "Verification of a backup of instance {Name} failed", context.Instance.Name);
        }
    }

    /// <summary>
    /// Ends the backups left running by a previous daemon process, and empties the folder of its temporary objects.
    /// </summary>
    private async Task EndLeftBackupsAsync(CancellationToken cancellationToken)
    {
        backupDestinationService.CleanTemporaryDirectory();
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<BackupRun> runs = await db.BackupRuns
            .Include(r => r.Destinations)
            .Where(r => r.Status == BackupRunStatus.Running)
            .ToListAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (BackupRun run in runs)
        {
            run.Status = BackupRunStatus.Interrupted;
            run.Error = "The daemon stopped during the backup.";
            run.FinishedAt = now;
            foreach (BackupRunDestination destination in run.Destinations)
            {
                if (destination.Status is BackupDestinationStatus.Pending or BackupDestinationStatus.Uploading)
                {
                    destination.Status = BackupDestinationStatus.Failed;
                    destination.Error = run.Error;
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
