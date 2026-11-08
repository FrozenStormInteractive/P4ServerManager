namespace P4ServerManager.Daemon.Services.Operations;

/// <param name="Message">Detail of what the step does at the moment.</param>
/// <param name="Percent">Progress of the step, when it can be measured.</param>
public readonly record struct OperationStepProgress(string? Message, int? Percent);

/// <summary>
/// One step of an operation run by the <see cref="OperationRunner"/>. A step does not know the operations it is part
/// of: an <see cref="IOperationDefinition{TContext}"/> puts it in its <see cref="OperationPlan{TContext}"/>, with the
/// conditions of that operation.
/// </summary>
/// <typeparam name="TContext">
/// What the step needs from the state of the operation. Typed on the smallest interface it needs, or on
/// <see cref="object"/> when it needs nothing, the same step fits the plan of any operation whose context implements
/// it.
/// </typeparam>
public interface IOperationStep<in TContext>
{
    /// <summary>
    /// Stable identifier, in kebab case. Clients use it to translate the step.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Title in English, shown by the clients that do not know the step.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Whether the step is part of this run at all, for a reason of its own: whether an operation includes it is
    /// decided by its plan. Read before the first step runs: a step that is not is left out of the list of the steps.
    /// </summary>
    bool IsIncluded(TContext context)
    {
        return true;
    }

    /// <summary>
    /// Whether the step applies to this run, for a reason of its own: when an operation skips it is decided by its
    /// plan. Read before the first step runs, and again right before the step runs.
    /// </summary>
    bool ShouldRun(TContext context)
    {
        return true;
    }

    /// <returns>A short summary of what was done, shown once the step succeeded.</returns>
    Task<string?> RunAsync(
        TContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken);
}
