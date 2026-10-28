namespace P4ServerManager.Daemon.Services.Perforce;

/// <summary>
/// A change to a p4d build is not allowed in its current state: it is in use, or it already exists.
/// </summary>
public sealed class P4ServerBuildConflictException(string message) : Exception(message);
