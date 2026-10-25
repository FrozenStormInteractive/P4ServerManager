using System.Text.RegularExpressions;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads the dump of the <c>db.config</c> table printed by <c>p4d -jd - db.config</c>, in the checkpoint format:
/// <c>@pv@ 1 @db.config@ @&lt;server&gt;@ @&lt;name&gt;@ @&lt;value&gt;@</c>.
/// </summary>
public static partial class ConfigDumpParser
{
    /// <summary>
    /// Whether the server runs in Unicode mode: <c>p4d -xi</c> sets the <c>unicode</c> configurable.
    /// </summary>
    public static bool IsUnicode(string dump)
    {
        return UnicodePattern().IsMatch(dump);
    }

    [GeneratedRegex(@"^@pv@ \d+ @db\.config@ @[^@]*@ @unicode@ @1@", RegexOptions.Multiline)]
    private static partial Regex UnicodePattern();
}
