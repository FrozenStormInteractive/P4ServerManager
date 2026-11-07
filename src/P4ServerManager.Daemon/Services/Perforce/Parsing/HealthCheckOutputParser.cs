using System.Text.RegularExpressions;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// A problem told by p4d during a check.
/// </summary>
/// <param name="Table">The table or the pair of tables, when p4d names one.</param>
/// <param name="Message">The problem, as p4d tells it.</param>
public sealed record HealthCheckProblem(string? Table, string Message);

/// <summary>
/// Reads what <c>p4d -xv</c> and <c>p4d -xx</c> print, one line at a time, as it comes.
/// <para>
/// The standard output tells the tables as they are reached: <c>Validating db.rev</c>, <c>Check db.rev/db.change...</c>.
/// An indented line there is a problem of the current table:
/// <c>**** Checksum mismatch **** possible data corruption</c>.
/// </para>
/// <para>
/// The error output is made of blocks of indented lines, each after a <c>Perforce server error:</c> line. A block that
/// names a table is a problem of the database: <c>db.change/db.desc inconsistencies found.</c>, <c>Table db.desc not
/// known.</c>. Any other block says that p4d could not do its work, such as a root it cannot read.
/// </para>
/// </summary>
public sealed partial class HealthCheckOutputParser
{
    private const string ErrorHeader = "Perforce server error:";

    private readonly List<string> block = [];
    private readonly List<string> errors = [];

    /// <summary>
    /// Table or pair of tables the check is on.
    /// </summary>
    public string? CurrentItem { get; private set; }

    /// <summary>
    /// Tables or pairs of tables the check has reached.
    /// </summary>
    public int ItemsChecked { get; private set; }

    /// <summary>
    /// What p4d told that is not about a table.
    /// </summary>
    public IReadOnlyList<string> Errors => errors;

    /// <summary>
    /// Reads a line of the standard output.
    /// </summary>
    /// <returns>The problem it tells, if any.</returns>
    public HealthCheckProblem? ReadOutputLine(string line)
    {
        if (ItemLine().Match(line) is { Success: true } item)
        {
            CurrentItem = item.Groups["item"].Value;
            ItemsChecked++;
            return null;
        }

        string message = line.Trim();
        return message.Length > 0 && char.IsWhiteSpace(line[0])
            ? new HealthCheckProblem(CurrentItem, message)
            : null;
    }

    /// <summary>
    /// Reads a line of the error output.
    /// </summary>
    /// <returns>The problem told by the block this line closes, if any.</returns>
    public HealthCheckProblem? ReadErrorLine(string line)
    {
        string text = line.Trim();
        if (text.Length == 0)
        {
            return null;
        }
        if (text != ErrorHeader)
        {
            block.Add(text);
            return null;
        }
        return CloseBlock();
    }

    /// <summary>
    /// Ends the reading of the error output.
    /// </summary>
    /// <returns>The problem told by the last block, if any.</returns>
    public HealthCheckProblem? Complete()
    {
        return CloseBlock();
    }

    private HealthCheckProblem? CloseBlock()
    {
        if (block.Count == 0)
        {
            return null;
        }

        string message = string.Join(' ', block);
        block.Clear();

        // Only counts the tables whose problems are already told.
        if (ProblemsSummary().IsMatch(message))
        {
            return null;
        }

        Match table = TableName().Match(message);
        if (table.Success)
        {
            return new HealthCheckProblem(table.Value, message);
        }
        errors.Add(message);
        return null;
    }

    [GeneratedRegex(@"^(?:Validating (?<item>\S+)|Check (?<item>\S+?)\.\.\.)\s*$")]
    private static partial Regex ItemLine();

    [GeneratedRegex(@"^Problems were found in \d+ table")]
    private static partial Regex ProblemsSummary();

    [GeneratedRegex(@"\bdb\.[\w.%-]+?(?:/db\.[\w.%-]+?)?(?=[\s!:,;]|\.?$|\.\s)")]
    private static partial Regex TableName();
}
