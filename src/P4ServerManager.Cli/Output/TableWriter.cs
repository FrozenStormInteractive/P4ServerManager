namespace P4ServerManager.Cli.Output;

/// <summary>
/// Writes rows as columns aligned on their widest cell.
/// </summary>
internal sealed class TableWriter(params string[] headers)
{
    private const string ColumnSeparator = "  ";

    private readonly List<string[]> rows = [headers];

    public void AddRow(params string[] cells)
    {
        if (cells.Length != headers.Length)
        {
            throw new ArgumentException($"A row needs {headers.Length} cells.", nameof(cells));
        }
        rows.Add(cells);
    }

    public void Write(TextWriter writer)
    {
        int[] widths = new int[headers.Length];
        foreach (string[] row in rows)
        {
            for (int i = 0; i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        foreach (string[] row in rows)
        {
            // The last column is not padded, so that no line ends with spaces.
            string line = string.Join(
                ColumnSeparator,
                row.Select((cell, i) => i == row.Length - 1 ? cell : cell.PadRight(widths[i])));
            writer.WriteLine(line.TrimEnd());
        }
    }
}
