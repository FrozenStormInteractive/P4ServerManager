namespace P4ServerManager.Daemon.Services.Perforce;

/// <summary>
/// A p4d build could not be downloaded or checked.
/// </summary>
public sealed class P4ServerBuildException(string message) : Exception(message);
