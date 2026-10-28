using System.Globalization;

using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Output;

internal static class BuildFormatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB"];

    /// <summary>
    /// The build as Perforce writes it, such as <c>2025.1/2850123</c>.
    /// </summary>
    public static string FormatVersion(BuildDto build)
    {
        return $"{build.Version}/{build.Build}";
    }

    /// <summary>
    /// A size in bytes, in the largest unit that keeps it above 1, such as <c>12.4 MB</c>.
    /// </summary>
    public static string FormatSize(long? bytes)
    {
        if (bytes is not long size)
        {
            return "-";
        }

        double value = size;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{size} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {Units[unit]}");
    }

    public static string FormatInstances(BuildDto build)
    {
        return build.Instances.Count == 0 ? "-" : string.Join(", ", build.Instances.Select(i => i.Name));
    }

    /// <summary>
    /// A build being fetched from Perforce, such as <c>r25.1, downloading p4d (42%)</c>.
    /// </summary>
    public static string FormatFetch(BuildFetchDto fetch)
    {
        string state = fetch.State switch
        {
            BuildFetchState.Queued => "queued",
            BuildFetchState.Checking => "checking",
            BuildFetchState.Downloading => $"downloading {fetch.FileName}",
            _ => $"compressing {fetch.Build}",
        };
        string text = $"{fetch.Release}, {state}";
        return fetch.Percent is { } percent ? string.Create(CultureInfo.InvariantCulture, $"{text} ({percent}%)") : text;
    }

    public static string FormatDate(DateTimeOffset? date)
    {
        return date is { } value ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "-";
    }
}
