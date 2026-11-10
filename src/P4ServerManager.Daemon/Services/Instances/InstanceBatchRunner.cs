using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Services.Instances;

public enum InstanceAction
{
    Start,
    Stop,
    Restart,
}

/// <summary>
/// Starts, stops or restarts several instances at once, in parallel. Each instance goes through the
/// <see cref="InstanceSupervisor"/> as if it were alone: one failure does not stop the others.
/// </summary>
public sealed class InstanceBatchRunner(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    InstanceSupervisor supervisor,
    InstanceRuntimeRegistry runtimeRegistry)
{
    /// <summary>
    /// Runs <paramref name="action"/> on the selected instances. An instance already in the state asked for is skipped.
    /// With <see cref="InstanceSelectionRequest.All"/>, the selection is every ready instance, and a restart skips the
    /// stopped ones: it does not start the servers that were stopped on purpose.
    /// </summary>
    /// <returns>One result per instance, by name.</returns>
    /// <exception cref="KeyNotFoundException">An instance of the list does not exist. Nothing was run.</exception>
    public async Task<IReadOnlyList<InstanceActionResult>> RunAsync(
        InstanceAction action,
        InstanceSelectionRequest selection,
        CancellationToken cancellationToken)
    {
        List<P4ServerInstance> instances;
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            IQueryable<P4ServerInstance> query = db.Instances.AsNoTracking().Include(i => i.Build);
            if (selection.All)
            {
                query = query.Where(i => i.State == P4ServerInstanceState.Ready);
            }
            else
            {
                List<Guid> ids = selection.InstanceIds?.Distinct().ToList() ?? [];
                query = query.Where(i => ids.Contains(i.Id));
            }
            instances = await query.OrderBy(i => i.Name).ToListAsync(cancellationToken);
        }

        if (!selection.All)
        {
            List<Guid> missing = (selection.InstanceIds ?? []).Except(instances.Select(i => i.Id)).ToList();
            if (missing.Count > 0)
            {
                throw new KeyNotFoundException($"No instance has the identifier {string.Join(", ", missing)}.");
            }
        }

        return await Task.WhenAll(instances.Select(instance =>
            RunAsync(action, instance, selection.All, cancellationToken)));
    }

    private async Task<InstanceActionResult> RunAsync(
        InstanceAction action,
        P4ServerInstance instance,
        bool all,
        CancellationToken cancellationToken)
    {
        InstanceRuntimeStatus status = runtimeRegistry.Get(instance.Id);
        bool skipped = instance.State == P4ServerInstanceState.Ready && action switch
        {
            InstanceAction.Start => status == InstanceRuntimeStatus.Running,
            InstanceAction.Stop => status == InstanceRuntimeStatus.Stopped,
            _ => all && status == InstanceRuntimeStatus.Stopped,
        };
        if (skipped)
        {
            return new InstanceActionResult(
                await instance.ToDtoAsync(status, cancellationToken),
                InstanceActionOutcome.Skipped,
                null);
        }

        try
        {
            P4ServerInstance result = action switch
            {
                InstanceAction.Start => await supervisor.StartInstanceAsync(instance.Id, cancellationToken),
                InstanceAction.Stop => await supervisor.StopInstanceAsync(instance.Id, cancellationToken),
                _ => await supervisor.RestartInstanceAsync(instance.Id, cancellationToken),
            };
            return new InstanceActionResult(
                await result.ToDtoAsync(runtimeRegistry.Get(result.Id), cancellationToken),
                InstanceActionOutcome.Succeeded,
                null);
        }
        catch (Exception exception) when (exception is InstanceConflictException or InstanceOperationException)
        {
            return await FailedAsync(instance, exception.Message, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            return await FailedAsync(instance, $"Instance {instance.Name} was deleted.", cancellationToken);
        }
    }

    /// <summary>
    /// The instance is read again: a failed start or stop keeps its error as the last error of the instance.
    /// </summary>
    private async Task<InstanceActionResult> FailedAsync(
        P4ServerInstance instance,
        string error,
        CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        P4ServerInstance current = await db.Instances
            .AsNoTracking()
            .Include(i => i.Build)
            .FirstOrDefaultAsync(i => i.Id == instance.Id, cancellationToken)
            ?? instance;
        return new InstanceActionResult(
            await current.ToDtoAsync(runtimeRegistry.Get(current.Id), cancellationToken),
            InstanceActionOutcome.Failed,
            error);
    }
}
