using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Contracts;

/// <summary>
/// The first account, created while the daemon has none.
/// </summary>
public sealed record SetupRequest
{
    /// <summary>
    /// Name to sign in with: a letter or a digit, then up to 63 letters, digits, '.', '-', '_' or '@'.
    /// </summary>
    [Required]
    public string UserName { get; init; } = null!;

    /// <summary>
    /// Password, between 10 and 128 characters.
    /// </summary>
    [Required]
    public string Password { get; init; } = null!;

    // Keeps the password out of logs that print the record.
    public override string ToString()
    {
        return $"SetupRequest {{ UserName = {UserName} }}";
    }
}

/// <summary>
/// Signs in to the web interface.
/// </summary>
public sealed record LoginRequest
{
    [Required]
    public string UserName { get; init; } = null!;

    [Required]
    public string Password { get; init; } = null!;

    /// <summary>
    /// Keeps the session after the browser closes. Otherwise it ends with the browser.
    /// </summary>
    public bool RememberMe { get; init; }

    // Keeps the password out of logs that print the record.
    public override string ToString()
    {
        return $"LoginRequest {{ UserName = {UserName}, RememberMe = {RememberMe} }}";
    }
}

/// <summary>
/// Changes the password of the signed-in user.
/// </summary>
public sealed record ChangePasswordRequest
{
    /// <summary>
    /// Current password, to confirm that the user is the one who signed in.
    /// </summary>
    [Required]
    public string CurrentPassword { get; init; } = null!;

    /// <summary>
    /// New password, between 10 and 128 characters.
    /// </summary>
    [Required]
    public string NewPassword { get; init; } = null!;

    // Keeps the passwords out of logs that print the record.
    public override string ToString()
    {
        return "ChangePasswordRequest { }";
    }
}

/// <summary>
/// Adds an account to the web interface.
/// </summary>
public sealed record CreateUserRequest
{
    /// <summary>
    /// Name to sign in with: a letter or a digit, then up to 63 letters, digits, '.', '-', '_' or '@'. Unique whatever
    /// its case.
    /// </summary>
    [Required]
    public string UserName { get; init; } = null!;

    /// <summary>
    /// Password, between 10 and 128 characters.
    /// </summary>
    [Required]
    public string Password { get; init; } = null!;

    /// <summary>
    /// True so that the user manages the accounts. False when missing.
    /// </summary>
    public bool IsAdministrator { get; init; }

    // Keeps the password out of logs that print the record.
    public override string ToString()
    {
        return $"CreateUserRequest {{ UserName = {UserName}, IsAdministrator = {IsAdministrator} }}";
    }
}

/// <summary>
/// Sets a new password on an account, such as when its user forgot it.
/// </summary>
public sealed record SetUserPasswordRequest
{
    /// <summary>
    /// New password, between 10 and 128 characters.
    /// </summary>
    [Required]
    public string Password { get; init; } = null!;

    // Keeps the password out of logs that print the record.
    public override string ToString()
    {
        return "SetUserPasswordRequest { }";
    }
}

/// <summary>
/// Names an account administrator, or takes this right back.
/// </summary>
public sealed record SetUserAdministratorRequest
{
    /// <summary>
    /// True so that the user manages the accounts. False when missing. The last administrator cannot lose this right.
    /// </summary>
    public bool IsAdministrator { get; init; }
}
