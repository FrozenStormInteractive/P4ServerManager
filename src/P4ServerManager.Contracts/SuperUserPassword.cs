namespace P4ServerManager.Contracts;

/// <summary>
/// The strength p4d asks of a password from security level 2, checked the same way by the daemon and the CLI.
/// </summary>
public static class SuperUserPassword
{
    public const int MinLength = 8;

    public const string Requirement =
        "The password has at least 8 characters, from at least two of the classes upper case, lower case and "
        + "non-alphabetic.";

    /// <summary>
    /// Whether the password has at least <see cref="MinLength"/> characters, from at least two of the classes upper
    /// case, lower case and non-alphabetic.
    /// </summary>
    public static bool IsStrong(string password)
    {
        if (password.Length < MinLength)
        {
            return false;
        }
        bool upper = password.Any(char.IsUpper);
        bool lower = password.Any(char.IsLower);
        bool other = password.Any(c => !char.IsLetter(c));
        return (upper ? 1 : 0) + (lower ? 1 : 0) + (other ? 1 : 0) >= 2;
    }
}
