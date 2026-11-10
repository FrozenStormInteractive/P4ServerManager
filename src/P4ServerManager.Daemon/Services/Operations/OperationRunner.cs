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
    /// Runs <paramref name="operation"/> with the plan it gives for <paramref name="context"/>. The operation is begun
    /// before the first await.
    /// </summary>
    public Task RunAsync<TContext>(
        Guid instanceId,
        IOperationDefinition<TContext> operation,
        TContext context,
        CancellationToken cancellationToken)
    {
        return RunAsync(instanceId, operation.Kind, operation.Plan(context), context, cancellationToken);
    }

    /// <summary>
    /// Runs the steps of <paramref name="plan"/> that apply to <paramref name="context"/>, and stops at the first that
    /// throws. The steps not included in this run are left out of the operation, and a step that no longer applies when
    /// its turn comes is skipped. A context that implements <see cref="IOperationOutcome"/> gives the outcome of the
    /// operation once it succeeded. The exception is rethrown once the failure is reported, so that the caller can undo
    /// what it needs to.
    /// </summary>
    public async Task RunAsync<TContext>(
        Guid instanceId,
        string kind,
        OperationPlan<TContext> plan,
        TContext context,
        CancellationToken cancellationToken)
    {
        // A step is known by its position: the same step can be in a plan more than once.
        List<PlannedStep<TContext>> includedSteps = plan.Steps.Where(s => s.Step.IsIncluded(context)).ToList();
        bool[] appliedAtStart = includedSteps.Select(s => Applies(s, context)).ToArray();
        await notifier.OperationChangedAsync(tracker.Begin(
            instanceId,
            kind,
            includedSteps.Select((s, index) => new OperationStepState(
                s.Step.Key,
                s.Step.Title,
                appliedAtStart[index] ? OperationStepStatus.Pending : OperationStepStatus.Skipped,
                null))));

        InlineProgress<OperationStepProgress> progress = new(p =>
            _ = notifier.OperationChangedAsync(tracker.ReportProgress(instanceId, p.Message, p.Percent)));
        try
        {
            for (int index = 0; index < includedSteps.Count; index++)
            {
                if (!appliedAtStart[index])
                {
                    continue;
                }

                PlannedStep<TContext> planned = includedSteps[index];
                if (!Applies(planned, context))
                {
                    await notifier.OperationChangedAsync(tracker.SkipStep(instanceId, index));
                    continue;
                }

                await notifier.OperationChangedAsync(tracker.StartStep(instanceId, index));
                string? message = await planned.Step.RunAsync(context, progress, cancellationToken);
                await notifier.OperationChangedAsync(tracker.CompleteStep(instanceId, index, message));
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

    /// <summary>
    /// Whether neither the plan nor the step itself skips the step.
    /// </summary>
    private static bool Applies<TContext>(PlannedStep<TContext> planned, TContext context)
    {
        return planned.SkipWhen?.Invoke(context) != true && planned.Step.ShouldRun(context);
    }
}
