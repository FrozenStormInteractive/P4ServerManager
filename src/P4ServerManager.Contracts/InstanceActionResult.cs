namespace P4ServerManager.Contracts;

/// <summary>
/// How a start, a stop or a restart ended for one instance of a selection.
/// </summary>
public enum InstanceActionOutcome
{
    Succeeded,

    /// <summary>
    /// Nothing to do: the instance already runs for a start, is already stopped for a stop, or is stopped for a restart
    /// of all the instances.
    /// </summary>
    Skipped,
    Failed,
}

/// <summary>
/// Result of a start, a stop or a restart on one instance of a selection.
/// </summary>
/// <param name="Instance">The instance as it is once the action ended.</param>
/// <param name="Outcome">Whether the action succeeded, was not needed, or failed.</param>
/// <param name="Error">Why the action failed. Null otherwise.</param>
public sealed record InstanceActionResult(InstanceDto Instance, InstanceActionOutcome Outcome, string? Error);
