using System.Globalization;
using System.Text;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads the records of a table dump printed by <c>p4d -jd - &lt;table&gt;</c>, in the checkpoint format: one record per
/// line, fields separated by a space, text between <c>@</c> with <c>@@</c> for a literal <c>@</c> (and possibly line
/// breaks), numbers bare. The first fields of a record are its operation (<c>pv</c>), the version of the table and its
/// name: <c>@pv@ 9 @db.depot@ @depot@ 0 @@ @depot/...@ ...</c>.
/// </summary>
public static class CheckpointRecordParser
{
    /// <summary>
    /// Kind of depot whose files the server keeps: local, spec, stream, archive, unload and tangent. Remote, graph and
    /// extension depots keep none in their map.
    /// </summary>
    private static readonly HashSet<int> DepotTypesWithArchives = [0, 2, 3, 4, 5, 6];

    /// <summary>
    /// Every record of the dump, as its fields.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string dump)
    {
        List<IReadOnlyList<string>> records = [];
        List<string> fields = [];
        StringBuilder value = new();
        int i = 0;
        while (i < dump.Length)
        {
            char c = dump[i];
            if (c == '\n' || c == '\r')
            {
                if (fields.Count > 0)
                {
                    records.Add(fields);
                    fields = [];
                }
                i++;
            }
            else if (c == ' ' || c == '\t')
            {
                i++;
            }
            else if (c == '@')
            {
                value.Clear();
                i++;
                while (i < dump.Length)
                {
                    if (dump[i] == '@')
                    {
                        if (i + 1 < dump.Length && dump[i + 1] == '@')
                        {
                            value.Append('@');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    value.Append(dump[i]);
                    i++;
                }
                fields.Add(value.ToString());
            }
            else
            {
                int start = i;
                while (i < dump.Length && dump[i] is not (' ' or '\t' or '\n' or '\r'))
                {
                    i++;
                }
                fields.Add(dump[start..i]);
            }
        }
        if (fields.Count > 0)
        {
            records.Add(fields);
        }
        return records;
    }

    /// <summary>
    /// The configurables of a <c>db.config</c> dump: the ones of <paramref name="serverId"/> win over the ones of every
    /// server (<c>any</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadConfigurables(string dump, string? serverId)
    {
        Dictionary<string, string> forAny = new(StringComparer.Ordinal);
        Dictionary<string, string> forServer = new(StringComparer.Ordinal);
        foreach (IReadOnlyList<string> record in Records(dump, "db.config", 6))
        {
            string server = record[3];
            if (server == "any")
            {
                forAny[record[4]] = record[5];
            }
            else if (serverId is not null && server == serverId)
            {
                forServer[record[4]] = record[5];
            }
        }

        foreach ((string name, string value) in forServer)
        {
            forAny[name] = value;
        }
        return forAny;
    }

    /// <summary>
    /// The depots of a <c>db.depot</c> dump that keep files, with their map: <c>@pv@ v @db.depot@ @name@ type @extra@ @map@ ...</c>.
    /// </summary>
    public static IReadOnlyList<DepotRecord> ReadDepots(string dump)
    {
        List<DepotRecord> depots = [];
        foreach (IReadOnlyList<string> record in Records(dump, "db.depot", 7))
        {
            if (int.TryParse(record[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type)
                && DepotTypesWithArchives.Contains(type))
            {
                depots.Add(new DepotRecord(record[3], type, record[6]));
            }
        }
        return depots;
    }

    /// <summary>
    /// The value of a counter in a <c>db.counters</c> dump: <c>@pv@ v @db.counters@ @name@ @value@</c>.
    /// </summary>
    public static string? ReadCounter(string dump, string name)
    {
        return Records(dump, "db.counters", 5).LastOrDefault(r => r[3] == name)?[4];
    }

    private static IEnumerable<IReadOnlyList<string>> Records(string dump, string table, int minimumFields)
    {
        return Parse(dump).Where(r => r.Count >= minimumFields && r[0] == "pv" && r[2] == table);
    }
}

/// <param name="Name">Name of the depot.</param>
/// <param name="Type">Kind of depot, as p4d numbers it.</param>
/// <param name="Map">Where its files are, such as <c>depot/...</c>: relative to <c>server.depot.root</c> or the root.</param>
public sealed record DepotRecord(string Name, int Type, string Map);
