using System.Text.RegularExpressions;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads the configurables printed by <c>p4d -r &lt;root&gt; -cshow</c>, one per line: <c>&lt;server&gt;: &lt;name&gt; = &lt;value&gt;</c>,
/// where the server is <c>any</c> for a value of every server.
/// </summary>
public static partial class ConfigShowParser
{
    /// <summary>
    /// The configurables of <paramref name="serverId"/>: its own values win over the ones of every server.
    /// </summary>
    /// <param name="output">What <c>p4d -cshow</c> printed.</param>
    /// <param name="serverId">Server ID of p4d, or null when it has none.</param>
    /// <exception cref="InvalidDataException">A line is not a configurable: the output is not what this parser knows.</exception>
    public static IReadOnlyDictionary<string, string> Parse(string output, string? serverId)
    {
        Dictionary<string, string> forAny = new(StringComparer.Ordinal);
        Dictionary<string, string> forServer = new(StringComparer.Ordinal);
        foreach (Configurable configurable in Read(output))
        {
            if (configurable.Server == "any")
            {
                forAny[configurable.Name] = configurable.Value;
            }
            else if (serverId is not null && configurable.Server == serverId)
            {
                forServer[configurable.Name] = configurable.Value;
            }
        }

        foreach ((string name, string value) in forServer)
        {
            forAny[name] = value;
        }
        return forAny;
    }

    /// <summary>
    /// The values set for <paramref name="serverId"/> itself, without the ones of every server.
    /// </summary>
    /// <param name="output">What <c>p4d -cshow</c> printed.</param>
    /// <param name="serverId">The server ID.</param>
    /// <exception cref="InvalidDataException">A line is not a configurable: the output is not what this parser knows.</exception>
    public static IReadOnlyDictionary<string, string> ParseOwn(string output, string serverId)
    {
        Dictionary<string, string> own = new(StringComparer.Ordinal);
        foreach (Configurable configurable in Read(output).Where(c => c.Server == serverId))
        {
            own[configurable.Name] = configurable.Value;
        }
        return own;
    }

    /// <summary>
    /// Every line of the output, in order, read as it is enumerated: enumerate it once.
    /// </summary>
    /// <exception cref="InvalidDataException">A line is not a configurable, thrown when it is reached.</exception>
    private static IEnumerable<Configurable> Read(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                continue;
            }

            Match match = ConfigurableLine().Match(trimmed);
            if (!match.Success)
            {
                throw new InvalidDataException($"p4d -cshow printed a line that is not a configurable: {trimmed}");
            }
            yield return new Configurable(
                match.Groups["server"].Value,
                match.Groups["name"].Value,
                match.Groups["value"].Value);
        }
    }

    private sealed record Configurable(string Server, string Name, string Value);

    // Blanks may vary before the value, never inside it: a single blank follows the equal sign.
    [GeneratedRegex(@"^(?<server>[^\s:]+):\s+(?<name>[^\s=]+)\s+=\s(?<value>.*)$")]
    private static partial Regex ConfigurableLine();
}
