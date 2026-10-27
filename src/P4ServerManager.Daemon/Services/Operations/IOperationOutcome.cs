namespace P4ServerManager.Daemon.Services.Operations;

/// <summary>
/// Context of an operation that can succeed in more than one way. The <see cref="OperationRunner"/> reads
/// <see cref="Outcome"/> once every step succeeded, and gives it to the clients with the snapshot.
/// </summary>
public interface IOperationOutcome
{
    /// <summary>
    /// Stable identifier of how the operation ended, in kebab case, such as <c>up-to-date</c>. Clients use it to tell
    /// the user.
    /// </summary>
    string? Outcome { get; }
}
