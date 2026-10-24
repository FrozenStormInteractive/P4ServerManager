using System.Collections.Concurrent;

namespace P4ServerManager.Daemon.Services.Operations;

/// <summary>
/// Last operation snapshot of each instance, in memory, so that a client that connects late can read it back.
/// </summary>
public sealed class OperationTracker
{
    private readonly ConcurrentDictionary<Guid, OperationSnapshot> snapshots = new();

    public OperationSnapshot? Get(Guid instanceId)
    {
        return snapshots.GetValueOrDefault(instanceId);
    }

    public void Remove(Guid instanceId)
    {
        snapshots.TryRemove(instanceId, out _);
    }

    public OperationSnapshot Begin(Guid instanceId, string kind, IEnumerable<OperationStepState> steps)
    {
        OperationSnapshot snapshot = new(instanceId, kind, OperationStatus.Running, steps.ToList(), null, null, null);
        snapshots[instanceId] = snapshot;
        return snapshot;
    }

    public OperationSnapshot StartStep(Guid instanceId, string key)
    {
        return Update(instanceId, snapshot => snapshot with
        {
            Steps = WithStep(snapshot.Steps, key, OperationStepStatus.Running, null),
            CurrentStep = key,
            Percent = null,
        });
    }

    public OperationSnapshot ReportProgress(Guid instanceId, string? message, int? percent)
    {
        return Update(instanceId, snapshot => snapshot.CurrentStep is not { } key
            ? snapshot
            : snapshot with
            {
                Steps = WithStep(snapshot.Steps, key, OperationStepStatus.Running, message),
                Percent = percent,
            });
    }

    public OperationSnapshot CompleteStep(Guid instanceId, string key, string? message)
    {
        return Update(instanceId, snapshot => snapshot with
        {
            Steps = WithStep(snapshot.Steps, key, OperationStepStatus.Succeeded, message),
            CurrentStep = null,
            Percent = null,
        });
    }

    public OperationSnapshot Complete(Guid instanceId)
    {
        return Update(instanceId, snapshot => snapshot with { Status = OperationStatus.Succeeded });
    }

    public OperationSnapshot Fail(Guid instanceId, string error)
    {
        return Update(instanceId, snapshot => snapshot with
        {
            Status = OperationStatus.Failed,
            Steps = snapshot.CurrentStep is { } key
                ? WithStep(snapshot.Steps, key, OperationStepStatus.Failed, error)
                : snapshot.Steps,
            CurrentStep = null,
            Percent = null,
            Error = error,
        });
    }

    private OperationSnapshot Update(Guid instanceId, Func<OperationSnapshot, OperationSnapshot> update)
    {
        return snapshots.AddOrUpdate(
            instanceId,
            _ => throw new InvalidOperationException($"No operation in progress for instance {instanceId}."),
            (_, snapshot) => update(snapshot));
    }

    private static List<OperationStepState> WithStep(
        IReadOnlyList<OperationStepState> steps,
        string key,
        OperationStepStatus status,
        string? message)
    {
        return steps.Select(s => s.Key == key ? s with { Status = status, Message = message } : s).ToList();
    }
}
