using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <param name="Version">For example <c>2025.1</c>.</param>
/// <param name="BuildNumber">For example <c>2850123</c>.</param>
public sealed record P4ServerVersionInfo(string Version, string BuildNumber);

/// <summary>
/// Reads the version and build from the output of <c>p4d -V</c>, for example
/// <c>Rev. P4D/LINUX26X86_64/2025.1/2850123 (2025/09/28).</c>
/// </summary>
public static partial class P4ServerVersionParser
{
    public static bool TryParse(string output, [NotNullWhen(true)] out P4ServerVersionInfo? versionInfo)
    {
        Match match = RevisionPattern().Match(output);
        versionInfo = match.Success
            ? new P4ServerVersionInfo(match.Groups["version"].Value, match.Groups["build"].Value)
            : null;
        return versionInfo is not null;
    }

    // Some builds append a suffix to the version, for example 2024.2.PATCH.
    [GeneratedRegex(@"Rev\.\s+P4D/[^/\s]+/(?<version>\d{4}\.\d+)[^/\s]*/(?<build>\d+)")]
    private static partial Regex RevisionPattern();
}
