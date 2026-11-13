using System.Globalization;

namespace P4ServerManager.Daemon.Services.Backups;

public static class ByteSize
{
    /// <summary>
    /// A size for people, such as <c>1.5 GiB</c>.
    /// </summary>
    public static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
