using System.Globalization;
using System.Text.Json;

namespace P4ServerManager.Daemon.Services.Instances.Import;

/// <summary>
/// Reads a p4dctl-ng profile: a JSON object with <c>Name</c>, <c>ServerType</c>, <c>Owner</c>, <c>Execute</c>,
/// <c>Args</c>, <c>Enabled</c>, <c>Prefix</c> and an <c>Environment</c> map that holds P4ROOT, P4PORT and, with SSL,
/// P4SSLDIR.
/// </summary>
public static class LegacyProfileParser
{
    // Read from the profile. Owner, Execute and Prefix are replaced by the daemon: they are not reported.
    private static readonly HashSet<string> UsedVariables = new(StringComparer.OrdinalIgnoreCase) { "P4ROOT", "P4PORT" };

    // Read from the profile only when the server uses SSL.
    private const string SslDirectoryVariable = "P4SSLDIR";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <param name="id">File name of the profile.</param>
    /// <param name="content">Content of the file.</param>
    public static LegacyProfile Parse(string id, string content)
    {
        ProfileFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ProfileFile>(content, Options);
        }
        catch (JsonException)
        {
            file = null;
        }
        if (file is null)
        {
            return new LegacyProfile(id, null, null, null, false, true, false, [], ["The profile is not a p4dctl-ng profile."]);
        }

        List<string> problems = [];
        List<string> ignored = [];
        Dictionary<string, string?> environment = new(file.Environment ?? [], StringComparer.OrdinalIgnoreCase);

        if (file.ServerType is { } serverType && !serverType.Equals("p4d", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"Only p4d servers can be imported, not {serverType}.");
        }

        string? rootPath = environment.GetValueOrDefault("P4ROOT");
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            rootPath = null;
            problems.Add("The profile has no P4ROOT.");
        }
        else if (!Path.IsPathFullyQualified(rootPath))
        {
            problems.Add($"The P4ROOT {rootPath} is not an absolute path.");
        }

        int? port = null;
        bool ssl = false;
        if (environment.GetValueOrDefault("P4PORT") is { } p4Port && !string.IsNullOrWhiteSpace(p4Port))
        {
            port = ParsePort(p4Port, out ssl);
        }

        if (ssl && rootPath is not null && Path.IsPathFullyQualified(rootPath)
            && CheckSslDirectory(rootPath, environment.GetValueOrDefault(SslDirectoryVariable)) is { } sslProblem)
        {
            problems.Add(sslProblem);
        }

        ignored.AddRange(environment.Keys
            .Where(key => !UsedVariables.Contains(key)
                && !(ssl && key.Equals(SslDirectoryVariable, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal));

        string[] arguments = (file.Args ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool caseSensitive = !arguments.Contains("-C1", StringComparer.Ordinal);
        string[] otherArguments = arguments.Where(argument => argument != "-C1").ToArray();
        if (otherArguments.Length > 0)
        {
            ignored.Add($"Args: {string.Join(' ', otherArguments)}");
        }

        string? name = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileNameWithoutExtension(id) : file.Name.Trim();
        return new LegacyProfile(id, name, rootPath, port, ssl, caseSensitive, file.Enabled, ignored, problems);
    }

    /// <summary>
    /// Checks that the key and the certificate of the server are where an instance looks for them: the <c>ssl</c>
    /// folder of its root. P4SSLDIR defaults to it, and a relative P4SSLDIR is read from the root.
    /// </summary>
    /// <returns>Why the profile cannot be imported, or null when it can.</returns>
    private static string? CheckSslDirectory(string rootPath, string? sslDirectory)
    {
        if (string.IsNullOrWhiteSpace(sslDirectory))
        {
            return null;
        }

        string expected = Path.GetFullPath(ServerLayout.GetSslDirectory(rootPath));
        string actual = Path.GetFullPath(Path.Combine(rootPath, sslDirectory.Trim()));
        return PathComparer.Equals(Path.TrimEndingDirectorySeparator(actual), expected)
            ? null
            : $"The SSL key and certificate are in {sslDirectory.Trim()}. Move them to {expected} before the import.";
    }

    /// <summary>
    /// The port number at the end of a P4PORT such as <c>1666</c>, <c>tcp:1666</c>, <c>ssl:host:1666</c> or
    /// <c>tcp6:[::]:1666</c>.
    /// </summary>
    /// <param name="p4Port">Value of P4PORT.</param>
    /// <param name="secure">Set when the address uses SSL.</param>
    /// <returns>The port, or null when it cannot be read.</returns>
    public static int? ParsePort(string p4Port, out bool secure)
    {
        string[] parts = p4Port.Trim().Split(':');
        secure = parts.Length > 1 && parts[0].StartsWith("ssl", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is > 0 and <= 65535
            ? port
            : null;
    }

    private sealed record ProfileFile(
        string? Name,
        string? ServerType,
        string? Args,
        Dictionary<string, string?>? Environment,
        bool Enabled = true);
}
