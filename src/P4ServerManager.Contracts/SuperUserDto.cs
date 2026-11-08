namespace P4ServerManager.Contracts;

/// <summary>
/// Super user account the daemon stores for an instance, to administer its server. The password is stored encrypted
/// and is never returned.
/// </summary>
/// <param name="UserName">P4 user name.</param>
/// <param name="Status">Result of the last check of the account.</param>
/// <param name="CheckedAt">Date of the last check that reached the server. Null until checked.</param>
/// <param name="Error">Message of p4 when the last check failed. Null otherwise.</param>
public sealed record SuperUserDto(
    string UserName,
    SuperUserStatus Status,
    DateTimeOffset? CheckedAt,
    string? Error);

/// <summary>
/// Result of the last check of the stored super user account.
/// </summary>
public enum SuperUserStatus
{
    /// <summary>Not checked yet: the server did not run since the account was stored.</summary>
    Unchecked,

    /// <summary>The account logs in and has super access.</summary>
    Valid,

    /// <summary>The server refused the user name or the password.</summary>
    InvalidCredentials,

    /// <summary>The password has expired: it must be changed in P4, then given again to the daemon.</summary>
    PasswordExpired,

    /// <summary>The account logs in but does not have super access.</summary>
    InsufficientAccess,
}
