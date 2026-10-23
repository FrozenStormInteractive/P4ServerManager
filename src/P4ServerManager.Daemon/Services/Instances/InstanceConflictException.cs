namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// An operation is not allowed in the current state of the instance.
/// </summary>
public sealed class InstanceConflictException(string message) : Exception(message);
