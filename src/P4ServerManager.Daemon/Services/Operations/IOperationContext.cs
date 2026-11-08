namespace P4ServerManager.Daemon.Services.Operations;

/// <summary>
/// Context of an operation whose steps hand <see cref="Values"/> to each other.
/// </summary>
public interface IOperationContext
{
    /// <summary>
    /// What a step produced for the steps after it, in memory, for this run only.
    /// </summary>
    OperationValues Values { get; }
}
