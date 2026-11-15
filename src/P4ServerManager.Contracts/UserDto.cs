namespace P4ServerManager.Contracts;

/// <summary>
/// An account of the web interface. The password is never returned.
/// </summary>
/// <param name="Id">Identifier of the account.</param>
/// <param name="UserName">Name the user signs in with.</param>
/// <param name="CreatedAt">When the account was created.</param>
/// <param name="LastSignInAt">When the user last signed in, if ever.</param>
public sealed record UserDto(Guid Id, string UserName, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);

/// <summary>
/// Whether a user is signed in, and whether the daemon still waits for its first account.
/// </summary>
/// <param name="SetupRequired">
/// No account exists yet: the first one is created with <c>POST /api/auth/setup</c>.
/// </param>
/// <param name="User">The signed-in user. Null when nobody is signed in, or for the CLI through the socket.</param>
public sealed record AuthSessionDto(bool SetupRequired, UserDto? User);
