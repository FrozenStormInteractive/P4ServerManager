using System.Globalization;

using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Services.Perforce;

/// <summary>
/// Orders p4d builds. Build numbers are stored as text, and only compare as numbers.
/// </summary>
public static class P4ServerBuildNumber
{
    /// <returns>The build number, or 0 when it is not a number.</returns>
    public static long Parse(string buildNumber)
    {
        return long.TryParse(buildNumber, NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : 0;
    }

    /// <summary>
    /// Compares two builds by version, then by build number.
    /// </summary>
    public static int Compare(P4ServerBuild left, P4ServerBuild right)
    {
        int version = ParseVersion(left.Version).CompareTo(ParseVersion(right.Version));
        return version != 0 ? version : Parse(left.BuildNumber).CompareTo(Parse(right.BuildNumber));
    }

    private static Version ParseVersion(string version)
    {
        return Version.TryParse(version, out Version? parsed) ? parsed : new Version(0, 0);
    }
}
