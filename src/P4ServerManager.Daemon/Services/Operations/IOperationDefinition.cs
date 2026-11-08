namespace P4ServerManager.Daemon.Services.Operations;

/// <summary>
/// An operation: which steps it runs, in which order, and when it skips them. The steps come from the dependency
/// injection; the order and the conditions are written in <see cref="Plan"/>.
/// </summary>
/// <typeparam name="TContext">State shared by the steps of the operation.</typeparam>
public interface IOperationDefinition<TContext>
{
    /// <summary>
    /// Stable identifier of the operation, in kebab case, such as <c>upgrade</c>. Clients use it to tell the user.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// The steps of the run for <paramref name="context"/>.
    /// </summary>
    OperationPlan<TContext> Plan(TContext context);
}
