namespace P4ServerManager.Daemon.Services.Authentication;

/// <summary>
/// A change of the accounts is not allowed in their current state: the name is taken, the first account already exists,
/// or the account is the last one.
/// </summary>
public sealed class UserConflictException(string message) : Exception(message);

/// <summary>
/// The current password given to change it is not the right one.
/// </summary>
public sealed class UserPasswordMismatchException() : Exception("The current password is not correct.");
