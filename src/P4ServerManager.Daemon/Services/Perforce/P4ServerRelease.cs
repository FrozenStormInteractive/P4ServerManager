using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace P4ServerManager.Daemon.Services.Perforce;

/// <summary>
/// A p4d release as named on the Perforce FTP, for example <c>r25.1</c>.
/// </summary>
public readonly partial record struct P4ServerRelease(int Year, int Number) : IComparable<P4ServerRelease>
{
    /// <summary>
    /// Folder name on the FTP, for example <c>r25.1</c>.
    /// </summary>
    public string Name => $"r{Year:00}.{Number}";

    /// <summary>
    /// Version as reported by p4d, for example <c>2025.1</c>.
    /// </summary>
    public string Version => $"20{Year:00}.{Number}";

    public static bool TryParse([NotNullWhen(true)] string? value, out P4ServerRelease release)
    {
        Match match = value is null ? Match.Empty : ReleasePattern().Match(value);
        if (!match.Success)
        {
            release = default;
            return false;
        }

        release = new P4ServerRelease(
            int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture));
        return true;
    }

    public int CompareTo(P4ServerRelease other)
    {
        int year = Year.CompareTo(other.Year);
        return year != 0 ? year : Number.CompareTo(other.Number);
    }

    public static bool operator <(P4ServerRelease left, P4ServerRelease right) => left.CompareTo(right) < 0;

    public static bool operator >(P4ServerRelease left, P4ServerRelease right) => left.CompareTo(right) > 0;

    public static bool operator <=(P4ServerRelease left, P4ServerRelease right) => left.CompareTo(right) <= 0;

    public static bool operator >=(P4ServerRelease left, P4ServerRelease right) => left.CompareTo(right) >= 0;

    public override string ToString()
    {
        return Name;
    }

    [GeneratedRegex(@"^r(?<year>\d{2})\.(?<number>\d)$")]
    private static partial Regex ReleasePattern();
}
