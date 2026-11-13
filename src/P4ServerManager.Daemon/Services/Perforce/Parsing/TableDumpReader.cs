using System.Globalization;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads the rows of any table from its dump, printed by <c>p4d -jd - &lt;table&gt;</c>, against the schemas the caller
/// knows. The read is strict: a record of another kind, another table, a version no schema describes, or with another
/// number of columns fails it, rather than give values from the wrong columns.
/// </summary>
/// <remarks>
/// The kinds of records are the ones of the checkpoint format (https://portal.perforce.com/s/article/2426): a dump holds
/// rows (<c>pv</c>), notes (<c>nx</c>) and transaction markers (<c>ex</c>, <c>mx</c>). The other kinds (<c>rv</c>,
/// <c>dv</c>, <c>vv</c>, <c>dl</c>, and <c>put</c>, <c>del</c> before 98.1) belong to journals, never to a dump.
/// </remarks>
public static class TableDumpReader
{
    /// <summary>
    /// Fields of a record before its columns: its kind, the version of its table, and the name of its table.
    /// </summary>
    private const int HeaderFieldCount = 3;

    private const string Banner = "Dumping to ";

    /// <summary>
    /// Every row of the dump of a table.
    /// </summary>
    /// <param name="dump">What <c>p4d -jd - &lt;table&gt;</c> printed.</param>
    /// <param name="schemas">The versions of the table the caller knows, at least one.</param>
    /// <exception cref="InvalidDataException">The dump holds a record this read does not know for sure.</exception>
    public static IReadOnlyList<TableRow> Read(string dump, params IReadOnlyList<TableSchema> schemas)
    {
        if (schemas.Count == 0 || schemas.Any(s => s.Table != schemas[0].Table))
        {
            throw new ArgumentException("The schemas must describe versions of one table.", nameof(schemas));
        }
        string table = schemas[0].Table;

        // p4d tells where it dumps on the first line of its output, before the records.
        if (dump.StartsWith(Banner, StringComparison.Ordinal))
        {
            int end = dump.IndexOf('\n', StringComparison.Ordinal);
            dump = end < 0 ? string.Empty : dump[(end + 1)..];
        }

        List<TableRow> rows = [];
        foreach (IReadOnlyList<string> record in CheckpointRecordParser.Parse(dump))
        {
            switch (record[0])
            {
                case "nx" or "ex" or "mx":
                    continue;
                case "pv":
                    break;
                default:
                    throw new InvalidDataException(
                        $"The dump of {table} holds a record of kind {record[0]}, which is no row of a dump.");
            }

            if (record.Count < HeaderFieldCount || record[2] != table)
            {
                throw new InvalidDataException($"The dump of {table} holds a record of another table.");
            }

            TableSchema schema = schemas.FirstOrDefault(
                s => s.Version.ToString(CultureInfo.InvariantCulture) == record[1])
                ?? throw new InvalidDataException(
                    $"{table} is at version {record[1]}, and only version "
                    + string.Join(", ", schemas.Select(s => s.Version)) + " is known.");

            int columnCount = record.Count - HeaderFieldCount;
            if (columnCount != schema.Columns.Count)
            {
                throw new InvalidDataException(
                    $"A row of {table} has {columnCount} columns instead of {schema.Columns.Count}.");
            }
            rows.Add(new TableRow(schema, record.Skip(HeaderFieldCount).ToList()));
        }
        return rows;
    }
}

/// <summary>
/// The tables of p4d the daemon reads, with the versions it knows.
/// </summary>
public static class P4Tables
{
    /// <summary>
    /// <c>db.config</c>, version 1: the configurables of each server, <c>any</c> for all of them.
    /// </summary>
    public static readonly TableSchema Config = new("db.config", 1, ["serverName", "name", "value"]);

    /// <summary>
    /// <c>db.depot</c>, version 2, from 2017.2 to 2026.1 at least. <c>type</c> is a DepotType: 0 local, 1 remote,
    /// 2 spec, 3 stream, 4 archive, 5 unload, 6 tangent, 7 graph, 8 extension, 9 trait.
    /// </summary>
    public static readonly TableSchema Depot = new("db.depot", 2, ["name", "type", "extra", "map", "objAddr"]);
}

/// <summary>
/// The columns of one version of a table of p4d, as the schema documentation of Perforce lists them
/// (https://www.perforce.com/perforce/doc.current/schema/index.html). A dump gives the columns of a row by their place
/// only, and p4d may add columns in a new version of a table: a schema names them for one version, and only that one.
/// </summary>
/// <param name="Table">Name of the table, such as <c>db.depot</c>.</param>
/// <param name="Version">Version of the table these columns belong to.</param>
/// <param name="Columns">Names of the columns, in their order.</param>
public sealed record TableSchema(string Table, int Version, IReadOnlyList<string> Columns);

/// <summary>
/// A row of a table, read against its schema: its values by the names of their columns.
/// </summary>
public sealed class TableRow
{
    private readonly IReadOnlyList<string> values;

    internal TableRow(TableSchema schema, IReadOnlyList<string> values)
    {
        Schema = schema;
        this.values = values;
    }

    public TableSchema Schema { get; }

    /// <summary>
    /// The value of <paramref name="column"/>, as text.
    /// </summary>
    /// <exception cref="ArgumentException">The schema has no such column.</exception>
    public string this[string column]
    {
        get
        {
            int index = -1;
            for (int i = 0; i < Schema.Columns.Count; i++)
            {
                if (Schema.Columns[i] == column)
                {
                    index = i;
                    break;
                }
            }
            return index >= 0
                ? values[index]
                : throw new ArgumentException(
                    $"Version {Schema.Version} of {Schema.Table} has no column {column}.",
                    nameof(column));
        }
    }

    /// <summary>
    /// The value of <paramref name="column"/>, as a number.
    /// </summary>
    /// <exception cref="InvalidDataException">The value is not a number.</exception>
    public int GetInt32(string column)
    {
        string value = this[column];
        return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number)
            ? number
            : throw new InvalidDataException($"Column {column} of {Schema.Table} is not a number: {value}.");
    }
}
