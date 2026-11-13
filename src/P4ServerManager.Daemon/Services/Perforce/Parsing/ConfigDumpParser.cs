namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads the dump of the <c>db.config</c> table printed by <c>p4d -jd - db.config</c>.
/// </summary>
public static class ConfigDumpParser
{
    /// <summary>
    /// Whether the server runs in Unicode mode: <c>p4d -xi</c> sets the <c>unicode</c> configurable.
    /// </summary>
    /// <exception cref="InvalidDataException">The dump holds a record this read does not know for sure.</exception>
    public static bool IsUnicode(string dump)
    {
        return TableDumpReader.Read(dump, P4Tables.Config).Any(row => row["name"] == "unicode" && row["value"] == "1");
    }
}
