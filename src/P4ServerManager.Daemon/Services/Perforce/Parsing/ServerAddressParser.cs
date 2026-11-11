using System.Globalization;

namespace P4ServerManager.Daemon.Services.Perforce.Parsing;

/// <summary>
/// Reads the address of a server spec, such as <c>1666</c>, <c>ssl:1666</c> or <c>tcp:p4.example.com:1666</c>: an
/// optional protocol and host, then the port after the last colon.
/// </summary>
public static class ServerAddressParser
{
    /// <summary>
    /// Moves an address to another port, and keeps its protocol and host.
    /// </summary>
    /// <returns>The new address, or null when the address does not end with <paramref name="oldPort"/>.</returns>
    public static string? ChangePort(string address, int oldPort, int newPort)
    {
        string trimmed = address.Trim();
        int separator = trimmed.LastIndexOf(':');
        string port = trimmed[(separator + 1)..];
        if (port.Length == 0
            || !port.All(char.IsAsciiDigit)
            || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || value != oldPort)
        {
            return null;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{trimmed[..(separator + 1)]}{newPort}");
    }
}
