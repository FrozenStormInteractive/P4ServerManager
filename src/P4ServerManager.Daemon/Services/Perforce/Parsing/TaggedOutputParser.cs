using System.Text.Json;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads what p4 prints with <c>-Mj -ztag</c>: one JSON object per line, whose keys are the tagged fields of a record,
/// such as <c>{"ServerID":"main","Address":"ssl:1666"}</c>. A value that spans several lines, such as a description,
/// stays whole. The messages of p4, errors among them, are records too, told apart by their <c>code0</c> key.
/// </summary>
public static class TaggedOutputParser
{
    private const string MessageCodeKey = "code0";
    private const string MessageTextKey = "data";

    /// <summary>
    /// Reads the records of the output, without the messages. A value that is not a string, such as a number, is kept
    /// as its JSON text.
    /// </summary>
    /// <exception cref="FormatException">A line is not a JSON object.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseRecords(string output)
    {
        return ReadAll(output).Where(record => !record.ContainsKey(MessageCodeKey)).ToList();
    }

    /// <summary>
    /// Reads the text of the messages of the output, such as why p4 refused a command.
    /// </summary>
    /// <returns>The messages, one per line, or null when the output has none or cannot be read.</returns>
    public static string? ReadMessages(string output)
    {
        try
        {
            List<string> messages = ReadAll(output)
                .Where(record => record.ContainsKey(MessageCodeKey))
                .Select(record => record.GetValueOrDefault(MessageTextKey)?.Trim())
                .OfType<string>()
                .Where(message => message.Length > 0)
                .ToList();
            return messages.Count == 0 ? null : string.Join('\n', messages);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IEnumerable<Dictionary<string, string>> ReadAll(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            Dictionary<string, string> record = new(StringComparer.Ordinal);
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"p4 printed a line that is not a JSON object: {line.Trim()}");
                }
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    record[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()!
                        : property.Value.GetRawText();
                }
            }
            catch (JsonException exception)
            {
                throw new FormatException($"p4 printed a line that is not JSON: {line.Trim()}", exception);
            }
            yield return record;
        }
    }
}
