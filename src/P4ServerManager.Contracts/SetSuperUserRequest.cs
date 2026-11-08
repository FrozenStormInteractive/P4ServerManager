using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Contracts;

/// <summary>
/// Super user account the daemon stores for an instance: another account, or the new password of the same one after it
/// was changed in P4. The daemon does not change anything in P4.
/// </summary>
public sealed record SetSuperUserRequest
{
    /// <summary>
    /// P4 user name, with super access: a letter, then letters, digits, '.', '-' or '_'.
    /// </summary>
    [Required]
    public string UserName { get; init; } = null!;

    /// <summary>
    /// Current password of the user in P4. Stored encrypted.
    /// </summary>
    [Required]
    public string Password { get; init; } = null!;

    // Keeps the password out of logs that print the record.
    public override string ToString()
    {
        return $"SetSuperUserRequest {{ UserName = {UserName} }}";
    }
}
