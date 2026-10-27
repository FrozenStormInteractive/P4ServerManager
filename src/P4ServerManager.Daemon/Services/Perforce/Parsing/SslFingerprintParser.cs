using System.Text.RegularExpressions;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads what <c>p4d -Gf</c> prints: <c>Fingerprint: A5:C8:...:F8</c>.
/// </summary>
public static partial class SslFingerprintParser
{
    /// <returns>The fingerprint, or null when the output has none.</returns>
    public static string? Parse(string output)
    {
        Match match = FingerprintPattern().Match(output);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"^Fingerprint:\s*([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2})+)\s*$", RegexOptions.Multiline)]
    private static partial Regex FingerprintPattern();
}
