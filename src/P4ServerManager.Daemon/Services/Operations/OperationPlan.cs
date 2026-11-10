namespace P4ServerManager.Daemon.Services.Operations;

/// <summary>
/// A step of an <see cref="OperationPlan{TContext}"/>.
/// </summary>
/// <param name="Step">The step.</param>
/// <param name="SkipWhen">
/// Whether the operation skips the step. Read before the first step runs, and again right before the step runs, so
/// that a step an earlier one made unnecessary is skipped then.
/// </param>
public sealed record PlannedStep<TContext>(IOperationStep<TContext> Step, Func<TContext, bool>? SkipWhen);

/// <summary>
/// The ordered steps of one run of an operation, with the conditions under which the operation skips them.
/// </summary>
/// <typeparam name="TContext">State shared by the steps of the operation.</typeparam>
public sealed class OperationPlan<TContext>
{
    private readonly List<PlannedStep<TContext>> steps = [];

    public IReadOnlyList<PlannedStep<TContext>> Steps => steps;

    /// <summary>
    /// Adds a step after the others. The same step can be added more than once, such as to stop p4d, start it, then
    /// stop it again.
    /// </summary>
    /// <param name="step">The step. Any step typed on what <typeparamref name="TContext"/> implements fits.</param>
    /// <param name="skipWhen">Whether the operation skips the step: it is then shown as skipped.</param>
    public OperationPlan<TContext> Then(IOperationStep<TContext> step, Func<TContext, bool>? skipWhen = null)
    {
        steps.Add(new PlannedStep<TContext>(step, skipWhen));
        return this;
    }

    /// <summary>
    /// Adds a step after the others when <paramref name="include"/> is true. A step left out is not shown at all, for a
    /// step the clients would have no reason to show, such as one for an option the instance does not use.
    /// </summary>
    public OperationPlan<TContext> ThenIf(
        bool include,
        IOperationStep<TContext> step,
        Func<TContext, bool>? skipWhen = null)
    {
        return include ? Then(step, skipWhen) : this;
    }
}
