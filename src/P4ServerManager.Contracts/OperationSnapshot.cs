namespace P4ServerManager.Contracts;

public enum OperationStatus
{
    Running,
    Succeeded,
    Failed,
}

public enum OperationStepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

/// <param name="Key">Stable identifier of the step, such as <c>resolve-build</c>. Clients translate it.</param>
/// <param name="Title">Title in English, for the clients that do not know the step.</param>
/// <param name="Status">Its status.</param>
/// <param name="Message">Detail of what it does, or why it failed.</param>
public sealed record OperationStepState(string Key, string Title, OperationStepStatus Status, string? Message);

/// <summary>
/// Progress of a long operation on an instance, such as its provisioning.
/// </summary>
/// <param name="InstanceId">The instance.</param>
/// <param name="Kind">What the operation does, such as <c>provision</c>.</param>
/// <param name="Status">Whether it runs, succeeded or failed.</param>
/// <param name="Steps">Every step, in order, including the ones that will be skipped.</param>
/// <param name="CurrentStep">Key of the step running, or null between steps and once finished.</param>
/// <param name="Percent">Progress of the current step, when it can be measured.</param>
/// <param name="Error">Error that stopped the operation.</param>
/// <param name="Outcome">
/// How the operation ended, once it succeeded, for an operation that can succeed in more than one way: for an upgrade,
/// <c>upgraded</c>, or <c>up-to-date</c> when the instance already ran the latest build and nothing was changed. Null
/// otherwise.
/// </param>
public sealed record OperationSnapshot(
    Guid InstanceId,
    string Kind,
    OperationStatus Status,
    IReadOnlyList<OperationStepState> Steps,
    string? CurrentStep,
    int? Percent,
    string? Error,
    string? Outcome = null);
