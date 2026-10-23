namespace P4ServerManager.Daemon.Services.Operations;

/// <param name="Message">Detail of what the step does at the moment.</param>
/// <param name="Percent">Progress of the step, when it can be measured.</param>
public readonly record struct OperationStepProgress(string? Message, int? Percent);

/// <summary>
/// One step of an operation run by the <see cref="OperationRunner"/>. An operation is the ordered list of its steps.
/// </summary>
/// <typeparam name="TContext">State shared by the steps of the operation.</typeparam>
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
    /// Whether the step applies to this run. Read before the first step runs: a step that does not apply is shown as
    /// skipped from the start.
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
