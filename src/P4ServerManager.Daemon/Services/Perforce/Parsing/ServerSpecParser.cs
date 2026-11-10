using System.Text.RegularExpressions;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads and changes server specs: what <c>p4 -ztag servers</c> lists, and the spec that <c>p4 server -o</c> prints.
/// </summary>
public static partial class ServerSpecParser
{
    /// <summary>
    /// Reads the server IDs that <c>p4 -ztag servers</c> lists, one <c>... ServerID main</c> line per spec.
    /// </summary>
    public static IReadOnlyList<string> ParseServerIds(string output)
    {
        return ServerIdTagPattern().Matches(output).Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>
    /// Gives a server spec another server ID, and keeps the rest of it.
    /// </summary>
    /// <exception cref="FormatException">The spec has no <c>ServerID:</c> field.</exception>
    public static string ReplaceServerId(string spec, string serverId)
    {
        if (!ServerIdFieldPattern().IsMatch(spec))
        {
            throw new FormatException("The server spec has no ServerID field.");
        }
        return ServerIdFieldPattern().Replace(spec, $"ServerID:\t{serverId}", 1);
    }

    [GeneratedRegex(@"^\.\.\. ServerID (\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ServerIdTagPattern();

    [GeneratedRegex(@"^ServerID:[ \t]*[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex ServerIdFieldPattern();
}
