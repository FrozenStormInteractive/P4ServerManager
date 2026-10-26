namespace P4ServerManager.Cli.Prompts;

/// <summary>
/// Asks the person at the terminal.
/// </summary>
internal interface IPrompter
{
    /// <summary>
    /// True when someone can answer: the input of the CLI is a terminal.
    /// </summary>
    bool IsInteractive { get; }

    /// <summary>
    /// Reads a line without showing what is typed.
    /// </summary>
    string ReadSecret(string prompt);

    /// <summary>
    /// Asks a yes or no question. No is the default.
    /// </summary>
    bool Confirm(string question);
}
