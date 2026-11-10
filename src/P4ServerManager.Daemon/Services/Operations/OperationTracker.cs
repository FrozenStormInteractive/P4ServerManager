using System.Collections.Concurrent;

using P4ServerManager.Contracts;

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

    /// <summary>
    /// Whether an operation runs on any instance, such as a provisioning or an upgrade.
    /// </summary>
    public bool HasRunningOperation()
    {
        return snapshots.Values.Any(s => s.Status == OperationStatus.Running);
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

    /// <param name="instanceId">The instance.</param>
    /// <param name="index">Position of the step in the steps given to <see cref="Begin"/>.</param>
    public OperationSnapshot StartStep(Guid instanceId, int index)
    {
        return Update(instanceId, snapshot => snapshot with
        {
            Steps = WithStep(snapshot.Steps, index, OperationStepStatus.Running, null),
            CurrentStepIndex = index,
            Percent = null,
        });
    }

    public OperationSnapshot ReportProgress(Guid instanceId, string? message, int? percent)
    {
        return Update(instanceId, snapshot => snapshot.CurrentStepIndex is not { } index
            ? snapshot
            : snapshot with
            {
                Steps = WithStep(snapshot.Steps, index, OperationStepStatus.Running, message),
                Percent = percent,
            });
    }

    /// <param name="instanceId">The instance.</param>
    /// <param name="index">Position of the step in the steps given to <see cref="Begin"/>.</param>
    /// <param name="message">Summary of what the step did.</param>
    public OperationSnapshot CompleteStep(Guid instanceId, int index, string? message)
    {
        return Update(instanceId, snapshot => snapshot with
        {
            Steps = WithStep(snapshot.Steps, index, OperationStepStatus.Succeeded, message),
            CurrentStepIndex = null,
            Percent = null,
        });
    }

    /// <param name="instanceId">The instance.</param>
    /// <param name="index">Position of the step in the steps given to <see cref="Begin"/>.</param>
    public OperationSnapshot SkipStep(Guid instanceId, int index)
    {
        return Update(instanceId, snapshot => snapshot with
        {
            Steps = WithStep(snapshot.Steps, index, OperationStepStatus.Skipped, null),
        });
    }

    public OperationSnapshot Complete(Guid instanceId, string? outcome)
    {
        return Update(instanceId, snapshot => snapshot with { Status = OperationStatus.Succeeded, Outcome = outcome });
    }

    public OperationSnapshot Fail(Guid instanceId, string error)
    {
        return Update(instanceId, snapshot => snapshot with
        {
            Status = OperationStatus.Failed,
            Steps = snapshot.CurrentStepIndex is { } index
                ? WithStep(snapshot.Steps, index, OperationStepStatus.Failed, error)
                : snapshot.Steps,
            CurrentStepIndex = null,
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
        int index,
        OperationStepStatus status,
        string? message)
    {
        return steps.Select((s, i) => i == index ? s with { Status = status, Message = message } : s).ToList();
    }
}
