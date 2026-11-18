using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Deletion;

namespace P4ServerManager.Daemon.Services.Instances;

public sealed partial class InstanceSupervisor
{
    /// <summary>
    /// Begins the deletion of an instance. It runs in the background, once this returns: p4d is stopped if it runs, the
    /// files of the server are deleted when asked, the instance is removed, then the folders the daemon keeps for it.
    /// The instance stays <see cref="InstanceRuntimeStatus.Deleting"/> meanwhile, and the clients are told once it is
    /// gone. A failure leaves the instance, with the error as its last error. A daemon that stops in the middle carries
    /// the deletion on at its next start. A failed instance can be deleted, but not one that is being provisioned.
    /// </summary>
    /// <param name="instanceId">Identifier of the instance.</param>
    /// <param name="deleteFiles">
    /// Also deletes the files of its server, as <see cref="InstanceFilePlanner"/> plans them, wherever its server
    /// directory is. When the plan has a problem, nothing is begun. When they are kept, the log of a p4d without a logs
    /// folder is moved to the <c>logs</c> folder of its root first.
    /// </param>
    /// <param name="cancellationToken">Aborts the wait for the instance to be free and the plan.</param>
    /// <returns>The instance, once the deletion has begun.</returns>
    /// <exception cref="InstanceConflictException">
    /// The instance is being provisioned, another operation is in progress, or its files cannot be deleted.
    /// </exception>
    public async Task<P4ServerInstance> DeleteInstanceAsync(
        Guid instanceId,
        bool deleteFiles,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim? instanceLock = await AcquireLockAsync(instanceId, cancellationToken);
        DaemonDbContext? db = null;
        try
        {
            db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await FindAsync(db, instanceId, cancellationToken);
            if (instance.State == P4ServerInstanceState.Provisioning)
            {
                throw new InstanceConflictException($"Instance {instance.Name} is being provisioned.");
            }

            InstanceFilePlan? plan = null;
            if (deleteFiles)
            {
                List<string> otherRoots = await db.Instances
                    .Where(i => i.Id != instance.Id)
                    .Select(i => i.RootPath)
                    .ToListAsync(cancellationToken);
                plan = await filePlanner.PlanAsync(instance, otherRoots, cancellationToken);
                if (plan.Problem is { } problem)
                {
                    throw new InstanceConflictException($"The files of instance {instance.Name} cannot be deleted. {problem}");
                }
            }

            InstanceDeletionState state = new(plan);
            await state.SaveAsync(paths.GetDeletionStatePath(instance.Id), cancellationToken);
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.Deleting);
            await notifier.InstanceChangedAsync(instance);
            logger.LogInformation(
                "Deleting instance {Name}{Files}",
                instance.Name,
                deleteFiles ? " with its files" : string.Empty);

            // The deletion outlives the request: it releases the instance and the context once it ends.
            _ = RunDeletionAsync(db, instance, instanceLock, state);
            instanceLock = null;
            db = null;
            return instance;
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
    /// Carries on the deletions a previous daemon process left unfinished, and removes the folders of the instances
    /// whose deletion only had these folders left.
    /// </summary>
    private async Task ResumeDeletionsAsync(CancellationToken cancellationToken)
    {
        List<Guid> known;
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            known = await db.Instances.Select(i => i.Id).ToListAsync(cancellationToken);
        }

        foreach (Guid instanceId in known)
        {
            string statePath = paths.GetDeletionStatePath(instanceId);
            InstanceDeletionState? state;
            try
            {
                state = await InstanceDeletionState.LoadAsync(statePath, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                logger.LogError(exception, "Could not read the deletion of instance {Id} left unfinished", instanceId);
                continue;
            }
            if (state is null)
            {
                continue;
            }

            SemaphoreSlim instanceLock = await AcquireLockAsync(instanceId, cancellationToken);
            DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await FindAsync(db, instanceId, cancellationToken);
            runtimeRegistry.Set(instance.Id, InstanceRuntimeStatus.Deleting);
            logger.LogWarning("Carrying on the deletion of instance {Name}, left unfinished", instance.Name);
            _ = RunDeletionAsync(db, instance, instanceLock, state);
        }

        // The instance was removed, but not the folder that held its deletion.
        if (Directory.Exists(paths.InstancesDirectory))
        {
            foreach (string directory in Directory.EnumerateDirectories(paths.InstancesDirectory))
            {
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id)
                    && !known.Contains(id)
                    && File.Exists(paths.GetDeletionStatePath(id)))
                {
                    TryDeleteDaemonFolders(id);
                }
            }
        }
    }

    private async Task RunDeletionAsync(
        DaemonDbContext db,
        P4ServerInstance instance,
        SemaphoreSlim instanceLock,
        InstanceDeletionState state)
    {
        CancellationToken stoppingToken = lifetime.ApplicationStopping;
        bool deleted = false;
        try
        {
            await launcher.StopAsync(instance, stoppingToken);
            await Task.Run(() => DeleteFiles(instance, state, stoppingToken), CancellationToken.None);

            // Out of the change tracker, so that a failure here leaves the entity as it was.
            await db.Instances.Where(i => i.Id == instance.Id).ExecuteDeleteAsync(CancellationToken.None);
            deleted = true;
            TryDeleteDaemonFolders(instance.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "The daemon stops during the deletion of instance {Name}: it goes on at the next start",
                instance.Name);
        }
        catch (Exception exception)
        {
            // Any failure is caught: nothing awaits this task.
            logger.LogError(exception, "Could not delete instance {Name}", instance.Name);
            instance.LastError = exception is InstanceOperationException
                ? exception.Message
                : $"Instance {instance.Name} could not be deleted: {exception.Message}";
            // The instance stays as it is: deleting it again begins anew.
            TryDeleteFile(paths.GetDeletionStatePath(instance.Id));
        }

        try
        {
            if (deleted)
            {
                runtimeRegistry.Remove(instance.Id);
                operationTracker.Remove(instance.Id);
                await notifier.InstanceDeletedAsync(instance.Id);
                logger.LogInformation("Deleted instance {Name}", instance.Name);
            }
            else if (!stoppingToken.IsCancellationRequested)
            {
                runtimeRegistry.Set(instance.Id, ReadRuntimeStatus(instance));
                await db.SaveChangesAsync(CancellationToken.None);
                await notifier.InstanceChangedAsync(instance);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not report the deletion of instance {Name}", instance.Name);
        }
        finally
        {
            await db.DisposeAsync();
            instanceLock.Release();
        }
    }

    // The files of the server, or the log to keep with them, then what the daemon keeps for the instance but the
    // deletion state, which goes once the instance is removed.
    private void DeleteFiles(P4ServerInstance instance, InstanceDeletionState state, CancellationToken cancellationToken)
    {
        string statePath = paths.GetDeletionStatePath(instance.Id);
        try
        {
            if (state.Plan is { } plan)
            {
                InstanceFilePlanner.Delete(plan, cancellationToken);
                logger.LogInformation(
                    "Deleted the files of instance {Name}: {Count} folders and {FileCount} files",
                    instance.Name,
                    plan.Directories.Count + plan.EmptiedDirectories.Count,
                    plan.Files.Count);
            }
            else
            {
                KeepFallbackLog(instance);
            }

            InstanceFilePlanner.DeleteContents(paths.GetRuntimeInstanceDirectory(instance.Id), statePath, cancellationToken);
            InstanceFilePlanner.DeleteContents(paths.GetInstanceDirectory(instance.Id), statePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstanceOperationException(
                $"The files of instance {instance.Name} could not be deleted: {exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// Moves the log a p4d without a logs folder wrote in the folder the daemon keeps for it to the <c>logs</c> folder
    /// of its root, so that it stays with the files of the server. p4d creates no logs folder of its own: it writes to
    /// the file it is given, or to its standard error. A file already there keeps its name: the moved one gets the time
    /// of the move.
    /// </summary>
    private void KeepFallbackLog(P4ServerInstance instance)
    {
        string source = Path.GetDirectoryName(paths.GetLogPath(instance.Id))!;
        if (!Directory.Exists(source) || !Directory.EnumerateFiles(source).Any())
        {
            return;
        }
        if (!Directory.Exists(instance.RootPath))
        {
            logger.LogWarning(
                "The root of instance {Name} is gone: its log in {Source} is deleted with the instance",
                instance.Name,
                source);
            return;
        }

        string target = Path.Combine(instance.RootPath, ServerLayout.LogsFolder);
        Directory.CreateDirectory(target);
        string suffix = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                destination = Path.Combine(
                    target,
                    $"{Path.GetFileNameWithoutExtension(file)}.{suffix}{Path.GetExtension(file)}");
            }
            File.Move(file, destination);
        }
        logger.LogInformation("Moved the log of instance {Name} to {Target}", instance.Name, target);
    }

    private void TryDeleteDaemonFolders(Guid instanceId)
    {
        try
        {
            InstanceFilePlanner.DeleteTree(paths.GetRuntimeInstanceDirectory(instanceId), CancellationToken.None);
            InstanceFilePlanner.DeleteTree(paths.GetInstanceDirectory(instanceId), CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next start of the daemon tries again.
            logger.LogWarning(exception, "Could not delete the folders of the deleted instance {Id}", instanceId);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete {Path}", path);
        }
    }
}
