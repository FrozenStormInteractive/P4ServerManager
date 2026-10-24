namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// An operation on an instance failed, or is not allowed in its current state.
/// </summary>
public sealed class InstanceOperationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
