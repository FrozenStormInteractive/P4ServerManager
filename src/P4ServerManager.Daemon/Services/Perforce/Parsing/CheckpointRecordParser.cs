using System.Text;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads the records of a table dump printed by <c>p4d -jd - &lt;table&gt;</c>, in the checkpoint format: one record per
/// line, fields separated by a space, text between <c>@</c> with <c>@@</c> for a literal <c>@</c> (and possibly line
/// breaks), numbers bare. The first fields of a record are its operation (<c>pv</c>), the version of the table and its
/// name: <c>@pv@ 2 @db.depot@ @depot@ 0 @@ @depot/...@ @@</c>. <see cref="TableDumpReader"/> gives the columns their
/// names.
/// </summary>
public static class CheckpointRecordParser
{
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
}
