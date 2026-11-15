namespace P4ServerManager.Contracts;

/// <summary>
/// The length asked of the password of an account of the web interface, checked the same way by the daemon and the CLI.
/// </summary>
public static class UserPassword
{
    public const int MinLength = 10;

    public const int MaxLength = 128;

    public const string Requirement = "The password has between 10 and 128 characters.";

    /// <summary>
    /// Whether the password has between <see cref="MinLength"/> and <see cref="MaxLength"/> characters.
    /// </summary>
    public static bool IsValid(string password)
    {
        return password.Length is >= MinLength and <= MaxLength;
    }
}
