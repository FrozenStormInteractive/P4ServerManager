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

    public static string FormatYesNo(bool value)
    {
        return value ? "yes" : "no";
    }
}
