using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Services.Instances;

namespace P4ServerManager.Daemon.Services.Operations;

/// <summary>
/// Runs the steps of an operation in order. Every change is kept by the <see cref="OperationTracker"/> and pushed to
/// the clients.
/// </summary>
public sealed class OperationRunner(OperationTracker tracker, InstanceNotifier notifier)
{
    /// <summary>
    /// Runs the steps that apply to <paramref name="context"/>, and stops at the first that throws. The steps not
    /// included in this run are left out of the operation, and a step that no longer applies when its turn comes is
    /// skipped. A context that implements <see cref="IOperationOutcome"/> gives the outcome of the operation once it
    /// succeeded. The exception is
    /// rethrown once the failure is reported, so that the caller can undo what it needs to.
    /// </summary>
    public async Task RunAsync<TContext>(
        Guid instanceId,
        string kind,
        IReadOnlyList<IOperationStep<TContext>> steps,
        TContext context,
        CancellationToken cancellationToken)
    {
        List<IOperationStep<TContext>> includedSteps = steps.Where(step => step.IsIncluded(context)).ToList();
        List<IOperationStep<TContext>> stepsToRun = includedSteps.Where(step => step.ShouldRun(context)).ToList();
        await notifier.OperationChangedAsync(tracker.Begin(
            instanceId,
            kind,
            includedSteps.Select(step => new OperationStepState(
                step.Key,
                step.Title,
                stepsToRun.Contains(step) ? OperationStepStatus.Pending : OperationStepStatus.Skipped,
                null))));

        InlineProgress<OperationStepProgress> progress = new(p =>
            _ = notifier.OperationChangedAsync(tracker.ReportProgress(instanceId, p.Message, p.Percent)));
        try
        {
            foreach (IOperationStep<TContext> step in stepsToRun)
            {
                if (!step.ShouldRun(context))
                {
                    await notifier.OperationChangedAsync(tracker.SkipStep(instanceId, step.Key));
                    continue;
                }

                await notifier.OperationChangedAsync(tracker.StartStep(instanceId, step.Key));
                string? message = await step.RunAsync(context, progress, cancellationToken);
                await notifier.OperationChangedAsync(tracker.CompleteStep(instanceId, step.Key, message));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            await notifier.OperationChangedAsync(tracker.Fail(instanceId, exception.Message));
            throw;
        }

        string? outcome = context is IOperationOutcome withOutcome ? withOutcome.Outcome : null;
        await notifier.OperationChangedAsync(tracker.Complete(instanceId, outcome));
    }
}
