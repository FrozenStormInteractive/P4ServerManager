using System.Text;

namespace P4ServerManager.Cli.Prompts;

/// <summary>
/// Asks on the console. The questions go to the standard error, so that they never mix with a result on the standard
/// output.
/// </summary>
internal sealed class ConsolePrompter : IPrompter
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public string ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        StringBuilder secret = new();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return secret.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }
    }

    public bool Confirm(string question)
    {
        Console.Error.Write($"{question} [y/N] ");
        string? answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
