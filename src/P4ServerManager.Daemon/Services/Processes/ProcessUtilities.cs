using System.Diagnostics;
using System.Runtime.InteropServices;

namespace P4ServerManager.Daemon.Services.Processes;

/// <summary>
/// Operations on processes that are not children of the daemon.
/// </summary>
public static partial class ProcessUtilities
{
    private const int SigTerm = 15;

    public static bool IsAlive(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            return Directory.Exists($"/proc/{pid}");
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Path of the executable of a process, or null when it cannot be read.
    /// </summary>
    public static string? GetExecutablePath(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return new FileInfo($"/proc/{pid}/exe").LinkTarget;
            }

            using Process process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks a process to stop: SIGTERM on Unix, which p4d handles as a clean shutdown. Windows has no such signal, so
    /// the process is killed there (development only).
    /// </summary>
    public static void RequestStop(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill();
            return;
        }

        if (Kill(pid, SigTerm) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"Could not send SIGTERM to process {pid} (errno {error}).");
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
