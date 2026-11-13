namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// A depot, as <c>db.depot</c> keeps it.
/// </summary>
/// <param name="Name">Name of the depot.</param>
/// <param name="Type">Kind of depot, as p4d numbers it: <see cref="RemoteType"/> for a remote depot.</param>
/// <param name="Map">Where its files are, such as <c>depot/...</c>: relative to <c>server.depot.root</c> or the root.</param>
/// <param name="ObjectAddress">Where an object store keeps its archives instead of the disk, or empty.</param>
public sealed record DepotRecord(string Name, int Type, string Map, string ObjectAddress = "")
{
    /// <summary>
    /// A remote depot: its map points to another server, and it keeps no file here.
    /// </summary>
    public const int RemoteType = 1;

    /// <summary>
    /// Every depot of a dump of <c>db.depot</c>.
    /// </summary>
    /// <param name="dump">What <c>p4d -jd - db.depot</c> printed.</param>
    /// <exception cref="InvalidDataException">The dump holds a record this read does not know for sure.</exception>
    public static IReadOnlyList<DepotRecord> ReadAll(string dump)
    {
        return TableDumpReader.Read(dump, P4Tables.Depot)
            .Select(row => new DepotRecord(row["name"], row.GetInt32("type"), row["map"], row["objAddr"]))
            .ToList();
    }
}
