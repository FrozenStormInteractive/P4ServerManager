using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Backup.Manifests;
using P4ServerManager.Backup.Repository;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Restore;
using P4ServerManager.Daemon.Services.Instances.Admin;

namespace P4ServerManager.Daemon.Services.Instances;

// Restores of backup points over the data of the instances.
public sealed partial class InstanceSupervisor
{
    // The restore in progress on each instance, so that it can be canceled.
    private readonly ConcurrentDictionary<Guid, (Guid RunId, RestoreContext Context, CancellationTokenSource Cancellation)> restoreCancellations = new();

    /// <summary>
    /// Begins to replace the data of an instance with a backup point. It runs in the background once this returns: the
    /// instance stays <see cref="InstanceRuntimeStatus.Restoring"/> until it ends, and its operation tells its
    /// progress. The current data is kept aside until the restore succeeds, and put back if it fails, is canceled, or
    /// the daemon stops meanwhile.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="destinationId">The destination that holds the point.</param>
    /// <param name="snapshotKey">Key of the manifest of the point.</param>
    /// <param name="cancellationToken">Aborts the wait for the instance to be free.</param>
    /// <returns>The restore, saved as running.</returns>
    /// <exception cref="KeyNotFoundException">The instance or the destination does not exist.</exception>
    /// <exception cref="InstanceConflictException">The instance is not ready, or another operation is in progress.</exception>
    public async Task<RestoreRun> StartRestoreAsync(
        Guid instanceId,
        Guid destinationId,
        string snapshotKey,
        CancellationToken cancellationToken)
    {
        if (!BackupRepository.TryParseManifestKey(snapshotKey, out BackupSnapshotInfo? snapshot))
        {
            throw new ArgumentException($"{snapshotKey} is not the key of a backup point.", nameof(snapshotKey));
        }

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
            BackupDestination destination = await db.BackupDestinations
                .FirstOrDefaultAsync(d => d.Id == destinationId, cancellationToken)
                ?? throw new KeyNotFoundException($"Backup destination {destinationId} does not exist.");

            SuperUserCredentials? superUser;
            try
            {
                superUser = superUserService.GetStoredCredentials(instance);
            }
            catch (SuperUserUnavailableException)
            {
                superUser = null;
            }

            RestoreRun run = new()
            {
                Id = Guid.NewGuid(),
                InstanceId = instance.Id,
                InstanceName = instance.Name,
                Target = RestoreTarget.ExistingInstance,
                DestinationId = destination.Id,
                DestinationName = destination.Name,
                SnapshotKey = snapshotKey,
                PointId = snapshot.Id,
                PointCreatedAt = snapshot.CreatedAt,
                Status = RestoreRunStatus.Running,
                StartedAt = timeProvider.GetUtcNow(),
            };
            db.RestoreRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);

            RestoreContext context = new(db, instance, run, destination)
            {
                WasRunning = launcher.IsRunning(instance),
                SuperUser = superUser,
            };
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            restoreCancellations[instance.Id] = (run.Id, context, cancellation);
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.Restoring);
            await notifier.InstanceChangedAsync(instance);
            await restoreRunNotifier.SaveAsync(db, run);

            // The restore outlives the request: it releases the instance and the context once it ends.
            _ = RunInBackgroundAsync(db, instance, instanceLock, _ => RestoreAsync(context, cancellation));
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
    /// Asks a running restore to stop. Before the server starts on the restored data, the restore ends as
    /// <see cref="RestoreRunStatus.Canceled"/> and the previous data comes back; during the verification of the
    /// archives, only the verification stops, and the restore succeeds with a warning.
    /// </summary>
    /// <returns>Whether the restore was still running.</returns>
    public async Task<bool> CancelRestoreAsync(Guid instanceId, Guid runId)
    {
        if (!restoreCancellations.TryGetValue(instanceId, out (Guid RunId, RestoreContext Context, CancellationTokenSource Cancellation) active)
            || active.RunId != runId)
        {
            return false;
        }

        try
        {
            await (active.Context.Verifying ? active.Context.VerifyCancellation : active.Cancellation).CancelAsync();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // It ended in the meantime.
            return false;
        }
    }

    /// <summary>
    /// Runs a restore and keeps how it ended with the run, whatever happens. A failure or a cancellation puts the
    /// previous data back.
    /// </summary>
    private async Task RestoreAsync(RestoreContext context, CancellationTokenSource cancellation)
    {
        P4ServerInstance instance = context.Instance;
        RestoreRun run = context.Run;
        try
        {
            Exception? failure = await RunOperationAsync(
                restoreOperation,
                context,
                () => logger.LogInformation(
                    "Restoring backup point {Key} from {Destination} over instance {Name}",
                    run.SnapshotKey,
                    run.DestinationName,
                    instance.Name),
                cancellation.Token);
            if (failure is null)
            {
                run.Status = context.Outcome == RestoreContext.RestoredOutcome
                    ? RestoreRunStatus.Succeeded
                    : RestoreRunStatus.SucceededWithProblems;
                logger.LogInformation("Restored instance {Name}: {Status}", instance.Name, run.Status);
            }
            else
            {
                logger.LogError(failure, "Restore of instance {Name} failed", instance.Name);
                run.Status = RestoreRunStatus.Failed;
                run.Error = failure.Message;
                instance.LastError = $"The restore failed: {failure.Message}";
                await RollBackRestoreAsync(context);
            }
        }
        catch (OperationCanceledException)
        {
            bool stopping = lifetime.ApplicationStopping.IsCancellationRequested;
            run.Status = stopping ? RestoreRunStatus.Interrupted : RestoreRunStatus.Canceled;
            run.Error = stopping ? "The daemon stopped during the restore." : "The restore was canceled.";
            logger.LogWarning("Restore of instance {Name} {Outcome}", instance.Name, stopping ? "interrupted" : "canceled");
            if (!stopping)
            {
                // A daemon that stops leaves the rollback to its next start, from the state file.
                await RollBackRestoreAsync(context);
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
            run.FinishedAt = timeProvider.GetUtcNow();
            restoreCancellations.TryRemove(instance.Id, out _);
            await context.CloseAsync();
            cancellation.Dispose();
            await restoreRunNotifier.SaveAsync(context.Db, run);
        }
    }

    /// <summary>
    /// Puts the data of an instance back as it was before a restore, then starts p4d if it ran.
    /// </summary>
    private async Task RollBackRestoreAsync(RestoreContext context)
    {
        P4ServerInstance instance = context.Instance;
        try
        {
            await launcher.StopAsync(instance, CancellationToken.None);
            if (context.SetAside is { } setAside)
            {
                setAside.RollBack();
                context.SetAside = null;
                logger.LogInformation("Put the previous data of instance {Name} back", instance.Name);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not put the previous data of instance {Name} back", instance.Name);
            instance.LastError += $" Its previous data could not be put back: {exception.Message} "
                + $"The folders kept aside are listed in {paths.GetRestoreStatePath(instance.Id)}.";
            return;
        }

        if (context.WasRunning)
        {
            await RestoreRunningStateAsync(instance, running: true, CancellationToken.None);
        }
    }

    /// <summary>
    /// Puts back the data of the instances whose restore a previous daemon process left unfinished, and ends the
    /// restores it left running.
    /// </summary>
    private async Task EndLeftRestoresAsync(CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<P4ServerInstance> instances = await db.Instances.Include(i => i.Build).ToListAsync(cancellationToken);
        foreach (P4ServerInstance instance in instances)
        {
            string statePath = paths.GetRestoreStatePath(instance.Id);
            try
            {
                RestoreSetAside? setAside = await RestoreSetAside.LoadAsync(statePath, cancellationToken);
                if (setAside is null)
                {
                    continue;
                }

                await launcher.StopAsync(instance, cancellationToken);
                setAside.RollBack();
                instance.LastError = "The daemon stopped during a restore: the previous data was put back.";
                logger.LogWarning("Put back the data of instance {Name}, left by an unfinished restore", instance.Name);
                if (setAside.WasRunning && instance.State == P4ServerInstanceState.Ready)
                {
                    await launcher.StartAsync(instance, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Could not put back the data of instance {Name} after a restore", instance.Name);
                instance.LastError = "The daemon stopped during a restore, and the previous data could not be put back: "
                    + $"{exception.Message} The folders kept aside are listed in {statePath}.";
            }
            runtimeRegistry.Set(instance.Id, ReadRuntimeStatus(instance));
        }

        List<RestoreRun> runs = await db.RestoreRuns
            .Where(r => r.Status == RestoreRunStatus.Running)
            .ToListAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (RestoreRun run in runs)
        {
            run.Status = RestoreRunStatus.Interrupted;
            run.Error = "The daemon stopped during the restore.";
            run.FinishedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
