using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Instances.Admin;

/// <summary>
/// The server refused an account given to the daemon: it is not stored.
/// </summary>
/// <param name="status">Why: wrong credentials, expired password or no super access.</param>
/// <param name="message">What p4 said, or what is missing.</param>
public sealed class SuperUserRejectedException(SuperUserStatus status, string message) : Exception(message)
{
    public SuperUserStatus Status { get; } = status;
}

/// <summary>
/// The daemon cannot act as the super user of an instance: no account is stored, the stored one is not valid, or the
/// server does not run.
/// </summary>
public sealed class SuperUserUnavailableException(string message) : Exception(message);
