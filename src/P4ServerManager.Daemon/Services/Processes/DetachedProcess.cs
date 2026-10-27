using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace P4ServerManager.Daemon.Services.Processes;

/// <summary>
/// Starts processes through <c>posix_spawn</c>, in a session of their own, so that the signals sent to the session or
/// the process group of the daemon (Ctrl+C, the end of a terminal) do not reach them.
/// </summary>
[UnsupportedOSPlatform("windows")]
public static partial class DetachedProcess
{
    private const short PosixSpawnSetPGroup = 0x02;
    private const short PosixSpawnSetSigDef = 0x04;
    private const short PosixSpawnSetSigMask = 0x08;

    // Not in POSIX, but in glibc since 2.26 and in musl, with the same value.
    private const short PosixSpawnSetSid = 0x80;

    private const int EInval = 22;
    private const int EIntr = 4;
    private const int EChild = 10;
    private const int WNoHang = 1;
    private const int ORdOnly = 0;
    private const int OWrOnly = 0x1;
    private const int OCreat = 0x40;
    private const int OTrunc = 0x200;
    private const int SigHup = 1;
    private const int SigInt = 2;
    private const int SigQuit = 3;
    private const int SigPipe = 13;
    private const int SigTerm = 15;

    // posix_spawnattr_t, posix_spawn_file_actions_t and sigset_t are opaque. 336, 80 and 128 bytes on glibc and musl
    // for x64 and arm64: these sizes leave room to spare.
    private const int OpaqueSize = 1024;

    private static readonly int[] DefaultSignals = [SigHup, SigInt, SigQuit, SigPipe, SigTerm];

    /// <summary>
    /// Starts a program in a new session, or at least in a new process group when the C library cannot create a
    /// session. The standard input reads from <c>/dev/null</c>, the standard and error outputs go to
    /// <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="fileName">Program to start.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="outputPath">File of its standard and error outputs.</param>
    /// <param name="environment">Variables set on top of the environment of the daemon.</param>
    /// <returns>The pid of the started process. It is a child of the daemon until it exits.</returns>
    public static unsafe int Start(
        string fileName,
        IReadOnlyList<string> arguments,
        string outputPath,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        void* attributes = NativeMemory.AllocZeroed(OpaqueSize);
        void* fileActions = NativeMemory.AllocZeroed(OpaqueSize);
        void* signals = NativeMemory.AllocZeroed(OpaqueSize);
        List<IntPtr> strings = [];
        bool attributesInitialized = false;
        bool fileActionsInitialized = false;
        try
        {
            Check(PosixSpawnAttrInit(attributes), "posix_spawnattr_init");
            attributesInitialized = true;
            Check(PosixSpawnFileActionsInit(fileActions), "posix_spawn_file_actions_init");
            fileActionsInitialized = true;

            // Signals the daemon ignores, such as SIGPIPE in .NET, would stay ignored in the new program.
            SigEmptySet(signals);
            foreach (int signal in DefaultSignals)
            {
                SigAddSet(signals, signal);
            }
            Check(PosixSpawnAttrSetSigDefault(attributes, signals), "posix_spawnattr_setsigdefault");
            SigEmptySet(signals);
            Check(PosixSpawnAttrSetSigMask(attributes, signals), "posix_spawnattr_setsigmask");

            const short commonFlags = PosixSpawnSetSigDef | PosixSpawnSetSigMask;
            int error = PosixSpawnAttrSetFlags(attributes, commonFlags | PosixSpawnSetSid);
            if (error == EInval)
            {
                // A new process group with the pgid of the process: Ctrl+C and the signals sent to the group of the
                // daemon still do not reach it.
                Check(PosixSpawnAttrSetPGroup(attributes, 0), "posix_spawnattr_setpgroup");
                error = PosixSpawnAttrSetFlags(attributes, commonFlags | PosixSpawnSetPGroup);
            }
            Check(error, "posix_spawnattr_setflags");

            byte* devNull = Utf8(strings, "/dev/null");
            byte* output = Utf8(strings, outputPath);
            Check(
                PosixSpawnFileActionsAddOpen(fileActions, 0, devNull, ORdOnly, 0),
                "posix_spawn_file_actions_addopen");
            Check(
                PosixSpawnFileActionsAddOpen(fileActions, 1, output, OWrOnly | OCreat | OTrunc, 0b110_100_100),
                "posix_spawn_file_actions_addopen");
            Check(PosixSpawnFileActionsAddDup2(fileActions, 1, 2), "posix_spawn_file_actions_adddup2");

            byte** argv = Utf8Array(strings, [fileName, .. arguments]);
            byte** envp = Utf8Array(strings, GetEnvironment(environment));
            int pid;
            Check(PosixSpawn(&pid, Utf8(strings, fileName), fileActions, attributes, argv, envp), "posix_spawn");
            return pid;
        }
        finally
        {
            if (fileActionsInitialized)
            {
                _ = PosixSpawnFileActionsDestroy(fileActions);
            }
            if (attributesInitialized)
            {
                _ = PosixSpawnAttrDestroy(attributes);
            }
            foreach (IntPtr pointer in strings)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
            NativeMemory.Free(signals);
            NativeMemory.Free(fileActions);
            NativeMemory.Free(attributes);
        }
    }

    /// <summary>
    /// Waits for a process started by <see cref="Start"/> to exit, and reaps it.
    /// </summary>
    /// <returns>
    /// The exit code, -1 when a signal ended the process, or null when its status is not available: the runtime
    /// reaps every child when SIGCHLD was ignored at startup.
    /// </returns>
    /// <exception cref="TimeoutException">The process still runs after <paramref name="timeout"/>.</exception>
    public static async Task<int?> WaitForExitAsync(
        int pid,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            int result = WaitPid(pid, out int status, WNoHang);
            if (result == pid)
            {
                // WIFEXITED and WEXITSTATUS.
                return (status & 0x7f) == 0 ? (status >> 8) & 0xff : -1;
            }
            if (result < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == EChild)
                {
                    return null;
                }
                if (error != EIntr)
                {
                    throw new InvalidOperationException($"waitpid failed for process {pid} (errno {error}).");
                }
                continue;
            }

            if (elapsed.Elapsed > timeout)
            {
                throw new TimeoutException($"Process {pid} did not exit within {timeout.TotalSeconds} seconds.");
            }
            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    private static IEnumerable<string> GetEnvironment(IReadOnlyDictionary<string, string>? overrides)
    {
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (overrides is null || !overrides.ContainsKey((string)variable.Key))
            {
                yield return $"{variable.Key}={variable.Value}";
            }
        }
        foreach ((string name, string value) in overrides ?? new Dictionary<string, string>())
        {
            yield return $"{name}={value}";
        }
    }

    private static unsafe byte* Utf8(List<IntPtr> strings, string value)
    {
        IntPtr pointer = Marshal.StringToCoTaskMemUTF8(value);
        strings.Add(pointer);
        return (byte*)pointer;
    }

    private static unsafe byte** Utf8Array(List<IntPtr> strings, IEnumerable<string> values)
    {
        List<string> list = [.. values];
        IntPtr array = Marshal.AllocCoTaskMem(IntPtr.Size * (list.Count + 1));
        strings.Add(array);
        byte** pointers = (byte**)array;
        for (int i = 0; i < list.Count; i++)
        {
            pointers[i] = Utf8(strings, list[i]);
        }
        pointers[list.Count] = null;
        return pointers;
    }

    private static void Check(int error, string function)
    {
        // The posix_spawn functions return the error number instead of setting errno.
        if (error != 0)
        {
            throw new InvalidOperationException($"{function} failed (errno {error}).");
        }
    }

    [LibraryImport("libc", EntryPoint = "posix_spawn")]
    private static unsafe partial int PosixSpawn(
        int* pid,
        byte* path,
        void* fileActions,
        void* attributes,
        byte** argv,
        byte** envp);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_init")]
    private static unsafe partial int PosixSpawnAttrInit(void* attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    private static unsafe partial int PosixSpawnAttrDestroy(void* attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    private static unsafe partial int PosixSpawnAttrSetFlags(void* attributes, short flags);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setpgroup")]
    private static unsafe partial int PosixSpawnAttrSetPGroup(void* attributes, int processGroup);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setsigdefault")]
    private static unsafe partial int PosixSpawnAttrSetSigDefault(void* attributes, void* signals);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setsigmask")]
    private static unsafe partial int PosixSpawnAttrSetSigMask(void* attributes, void* signals);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    private static unsafe partial int PosixSpawnFileActionsInit(void* fileActions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static unsafe partial int PosixSpawnFileActionsDestroy(void* fileActions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addopen")]
    private static unsafe partial int PosixSpawnFileActionsAddOpen(
        void* fileActions,
        int fd,
        byte* path,
        int flags,
        uint mode);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    private static unsafe partial int PosixSpawnFileActionsAddDup2(void* fileActions, int fd, int newFd);

    [LibraryImport("libc", EntryPoint = "sigemptyset")]
    private static unsafe partial int SigEmptySet(void* signals);

    [LibraryImport("libc", EntryPoint = "sigaddset")]
    private static unsafe partial int SigAddSet(void* signals, int signal);

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static partial int WaitPid(int pid, out int status, int options);
}
