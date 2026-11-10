using System.Text.RegularExpressions;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads the server ID that <c>p4d -xD</c> prints when it gets no server ID to set.
/// </summary>
/// <remarks>
/// Perforce documents the message of a server without a server ID ("Server does not yet have a server ID"), not the
/// exact output of a server that has one. p4d may also answer as an error, "Server already has a server ID: main".
/// Labeled forms such as <c>Server ID: main</c>, and a bare server ID, are read. Any other output is refused rather than
/// guessed.
/// </remarks>
public static partial class ServerIdOutputParser
{
    /// <returns>The server ID, or null when the server has none.</returns>
    /// <exception cref="FormatException">The output is in no known form.</exception>
    public static string? Parse(string output)
    {
        if (NoServerIdPattern().IsMatch(output))
        {
            return null;
        }

        Match match = AlreadySetPattern().Match(output);
        if (!match.Success)
        {
            match = LabeledPattern().Match(output);
        }
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        string[] tokens = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1)
        {
            return tokens[0].Trim('\'', '"');
        }

        throw new FormatException($"Unexpected output of p4d -xD: {output.Trim()}");
    }

    /// <summary>
    /// Whether the output is one of the messages by which p4d tells whether it has a server ID. p4d may print them as
    /// errors, with a failed exit code.
    /// </summary>
    public static bool IsServerIdMessage(string output)
    {
        return NoServerIdPattern().IsMatch(output) || AlreadySetPattern().IsMatch(output);
    }

    [GeneratedRegex(@"does not (yet )?have a server ?ID", RegexOptions.IgnoreCase)]
    private static partial Regex NoServerIdPattern();

    [GeneratedRegex(@"already has a server ?ID\s*:?\s*['""]?([^\s'""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AlreadySetPattern();

    [GeneratedRegex(@"server ?ID(?:\s+is)?\s*[:=]?\s*['""]?([^\s'""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledPattern();
}
