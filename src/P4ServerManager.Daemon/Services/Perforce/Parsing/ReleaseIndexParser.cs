using System.Text.RegularExpressions;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads the release folders (<c>r25.1/</c>) from the HTML index of the Perforce FTP.
/// </summary>
public static partial class ReleaseIndexParser
{
    /// <returns>The distinct releases, newest first.</returns>
    public static IReadOnlyList<P4ServerRelease> Parse(string html)
    {
        SortedSet<P4ServerRelease> releases = [];
        foreach (Match match in ReleaseLinkPattern().Matches(html))
        {
            if (P4ServerRelease.TryParse(match.Groups["release"].Value, out P4ServerRelease release))
            {
                releases.Add(release);
            }
        }
        return releases.Reverse().ToList();
    }

    [GeneratedRegex(@"href=""(?<release>r\d{2}\.\d)/""", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseLinkPattern();
}
