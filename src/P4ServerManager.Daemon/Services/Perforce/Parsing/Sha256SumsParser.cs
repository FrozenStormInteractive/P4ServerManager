using System.Text.RegularExpressions;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads a <c>SHA256SUMS</c> file in the <c>sha256sum</c> format: <c>&lt;hash&gt; *&lt;file&gt;</c>.
/// </summary>
public static partial class Sha256SumsParser
{
    /// <returns>The lower case hashes by file name.</returns>
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        foreach (string line in content.Split('\n'))
        {
            Match match = LinePattern().Match(line.Trim());
            if (match.Success)
            {
                hashes[match.Groups["file"].Value] = match.Groups["hash"].Value.ToLowerInvariant();
            }
        }
        return hashes;
    }

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<file>\S.*)$")]
    private static partial Regex LinePattern();
}
