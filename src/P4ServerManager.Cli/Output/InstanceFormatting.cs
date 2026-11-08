using System.Globalization;

using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Output;

internal static class InstanceFormatting
{
    /// <summary>
    /// The pinned p4d build as Perforce writes it, such as <c>2025.1/2850123</c>, or the release asked for while the
    /// build is not resolved.
    /// </summary>
    public static string FormatVersion(InstanceDto instance)
    {
        return instance.Version is null ? instance.Release : $"{instance.Version}/{instance.Build}";
    }

    /// <summary>
    /// The port as clients give it in P4PORT: prefixed with <c>ssl:</c> when the instance uses SSL.
    /// </summary>
    public static string FormatPort(InstanceDto instance)
    {
        string port = instance.Port.ToString(CultureInfo.InvariantCulture);
        return instance.Ssl ? $"ssl:{port}" : port;
    }

    /// <summary>
    /// The runtime status, followed by <c>(maintenance)</c> when the instance is in maintenance mode.
    /// </summary>
    public static string FormatStatus(InstanceDto instance)
    {
        string status = instance.RuntimeStatus.ToString();
        return instance.MaintenanceMode ? $"{status} (maintenance)" : status;
    }

    /// <summary>
    /// The stored super user and the result of its last check, such as <c>admin (Valid)</c>, or null when none is
    /// stored.
    /// </summary>
    public static string? FormatSuperUser(InstanceDto instance)
    {
        return instance.SuperUser is { } superUser ? $"{superUser.UserName} ({superUser.Status})" : null;
    }

    public static string FormatYesNo(bool value)
    {
        return value ? "yes" : "no";
    }
}
