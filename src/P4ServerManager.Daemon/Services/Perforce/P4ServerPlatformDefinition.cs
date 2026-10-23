using System.Runtime.InteropServices;

namespace P4ServerManager.Daemon.Services.Perforce;

/// <summary>
/// Platform the daemon runs on, and the matching folder of the Perforce FTP.
/// </summary>
/// <param name="Name">Runtime identifier, for example <c>linux-x64</c>.</param>
/// <param name="FtpDirectory">Folder of a release on the FTP, for example <c>bin.linux26x86_64</c>.</param>
/// <param name="ExecutableSuffix"><c>.exe</c> on Windows, empty elsewhere.</param>
public sealed record P4ServerPlatformDefinition(string Name, string FtpDirectory, string ExecutableSuffix)
{
    public static readonly P4ServerPlatformDefinition LinuxX64 = new("linux-x64", "bin.linux26x86_64", "");

    public static readonly P4ServerPlatformDefinition LinuxArm64 = new("linux-arm64", "bin.linux26aarch64", "");

    /// <summary>
    /// Only meant for development: the package targets Linux.
    /// </summary>
    public static readonly P4ServerPlatformDefinition WindowsX64 = new("win-x64", "bin.ntx64", ".exe");

    public string P4dFileName => "p4d" + ExecutableSuffix;

    public string P4FileName => "p4" + ExecutableSuffix;

    public static P4ServerPlatformDefinition Detect()
    {
        Architecture architecture = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
        {
            return LinuxX64;
        }
        if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
        {
            return LinuxArm64;
        }
        if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
        {
            return WindowsX64;
        }

        throw new PlatformNotSupportedException(
            $"p4d is not available for {RuntimeInformation.OSDescription} ({architecture}).");
    }
}
