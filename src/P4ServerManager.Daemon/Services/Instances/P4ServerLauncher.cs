using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Starts and stops the p4d process of an instance. p4d is never a child that dies with the daemon: on Linux it forks
/// into the background (<c>--daemonsafe</c>) in its own session, and the daemon finds it again through its pid file.
/// </summary>
public sealed class P4ServerLauncher(
    DaemonPaths paths,
    IP4ServerBuildCache buildCache,
    ILogger<P4ServerLauncher> logger)
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Arguments shared by every p4d command run on the root of an instance.
    /// </summary>
    public static IReadOnlyList<string> GetRootArguments(P4ServerInstance instance)
    {
        List<string> arguments = ["-r", instance.RootPath];
        if (!instance.CaseSensitive)
        {
            arguments.Add("-C1");
        }
        return arguments;
    }

    /// <summary>
    /// What p4d listens on: the port, prefixed with <c>ssl:</c> when the instance uses SSL.
    /// </summary>
    public static string GetListenAddress(P4ServerInstance instance)
    {
        string port = instance.Port.ToString(CultureInfo.InvariantCulture);
        return instance.Ssl ? $"ssl:{port}" : port;
    }

    /// <summary>
    /// Variables p4d needs on top of the environment of the daemon: the folder of its key and certificate, with SSL.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? GetEnvironment(P4ServerInstance instance)
    {
        return instance.Ssl
            ? new Dictionary<string, string> { ["P4SSLDIR"] = ServerLayout.GetSslDirectory(instance.RootPath) }
            : null;
    }

    /// <summary>
    /// Log of p4d: <c>logs/log</c> in the server directory when it has a logs folder, the file P4LOG points to in a
    /// server laid out, otherwise a file of the instance in the data directory.
    /// </summary>
    public string GetLogPath(P4ServerInstance instance)
    {
        string? serverDirectory = ServerLayout.GetServerDirectory(instance.RootPath);
        if (serverDirectory is not null)
        {
            string logsDirectory = Path.Combine(serverDirectory, ServerLayout.LogsFolder);
            if (Directory.Exists(logsDirectory))
            {
                return Path.Combine(logsDirectory, ServerLayout.LogFile);
            }
        }
        return paths.GetLogPath(instance.Id);
    }

    /// <summary>
    /// Whether the p4d of the instance runs.
    /// </summary>
    public bool IsRunning(P4ServerInstance instance)
    {
        return GetRunningPid(instance) is not null;
    }

    /// <summary>
    /// Pid of the p4d of the instance when it runs, otherwise null.
    /// </summary>
    public int? GetRunningPid(P4ServerInstance instance)
    {
        string pidFile = paths.GetPidFilePath(instance.Id);
        if (!TryReadPid(pidFile, out int pid) || !ProcessUtilities.IsAlive(pid))
        {
            return null;
        }

        // The pid may have been reused by another program since p4d stopped.
        string? executable = ProcessUtilities.GetExecutablePath(pid);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (executable is not null && !executable.StartsWith(paths.BuildsDirectory, comparison))
        {
            return null;
        }
        return pid;
    }

    /// <returns>The pid of the started p4d.</returns>
    public async Task<int> StartAsync(P4ServerInstance instance, CancellationToken cancellationToken)
    {
        P4ServerBuild build = instance.Build
            ?? throw new InstanceConflictException($"Instance {instance.Name} has no p4d build yet.");
        if (IsRunning(instance))
        {
            throw new InstanceConflictException($"Instance {instance.Name} is already running.");
        }

        string p4dPath = buildCache.GetP4dPath(build);
        string pidFile = paths.GetPidFilePath(instance.Id);
        string logPath = GetLogPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.GetStartOutputPath(instance.Id))!);
        File.Delete(pidFile);

        List<string> arguments =
        [
            .. GetRootArguments(instance),
            "-p", GetListenAddress(instance),
            "-L", logPath,
        ];
        if (instance.MaintenanceMode)
        {
            arguments.Add("-n");
        }
        IReadOnlyDictionary<string, string>? environment = GetEnvironment(instance);

        logger.LogInformation(
            "Starting p4d for instance {Name} on port {Port}{Mode}",
            instance.Name,
            instance.Port,
            instance.MaintenanceMode ? " in maintenance mode" : string.Empty);
        int pid = OperatingSystem.IsWindows()
            ? StartOnWindows(p4dPath, arguments, environment, pidFile)
            : await StartOnUnixAsync(
                p4dPath,
                arguments,
                environment,
                pidFile,
                paths.GetStartOutputPath(instance.Id),
                cancellationToken);

        await WaitUntilListeningAsync(instance, pid, logPath, cancellationToken);
        logger.LogInformation("p4d of instance {Name} runs with pid {Pid}", instance.Name, pid);
        return pid;
    }

    public async Task StopAsync(P4ServerInstance instance, CancellationToken cancellationToken)
    {
        int? pid = GetRunningPid(instance);
        if (pid is null)
        {
            return;
        }

        logger.LogInformation("Stopping p4d of instance {Name} (pid {Pid})", instance.Name, pid);
        ProcessUtilities.RequestStop(pid.Value);

        Stopwatch elapsed = Stopwatch.StartNew();
        while (ProcessUtilities.IsAlive(pid.Value))
        {
            if (elapsed.Elapsed > StopTimeout)
            {
                throw new InstanceOperationException(
                    $"p4d of instance {instance.Name} did not stop within {StopTimeout.TotalSeconds} seconds.");
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
        File.Delete(paths.GetPidFilePath(instance.Id));
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<int> StartOnUnixAsync(
        string p4dPath,
        List<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string pidFile,
        string outputPath,
        CancellationToken cancellationToken)
    {
        // --daemonsafe forks and closes the standard streams, so the first process exits once p4d is in the
        // background. It starts in a session of its own: a Ctrl+C on the daemon, or the end of its session, does not
        // reach p4d.
        arguments.Add("--daemonsafe");
        arguments.Add($"--pid-file={pidFile}");
        int launcherPid = DetachedProcess.Start(p4dPath, arguments, outputPath, environment);

        int? exitCode;
        try
        {
            exitCode = await DetachedProcess.WaitForExitAsync(
                launcherPid,
                StartTimeout,
                PollInterval,
                cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InstanceOperationException(
                $"p4d did not go to the background within {StartTimeout.TotalSeconds} seconds.");
        }
        if (exitCode is not (0 or null))
        {
            throw new InstanceOperationException(
                $"p4d did not start (exit code {exitCode}). {ReadLogTail(outputPath)}".Trim());
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        int pid;
        while (!TryReadPid(pidFile, out pid))
        {
            if (elapsed.Elapsed > StartTimeout)
            {
                throw new InstanceOperationException("p4d started but did not write its pid file.");
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
        return pid;
    }

    private static int StartOnWindows(
        string p4dPath,
        List<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string pidFile)
    {
        // Windows is only a development target. p4d accepts -d and --daemonsafe there but does not fork: the process
        // stays in the foreground. So it is started without waiting, and the daemon writes the pid file itself.
        ProcessStartInfo startInfo = new(p4dPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InstanceOperationException("p4d did not start.");
        File.WriteAllText(pidFile, process.Id.ToString(CultureInfo.InvariantCulture));
        return process.Id;
    }

    private static async Task WaitUntilListeningAsync(
        P4ServerInstance instance,
        int pid,
        string logPath,
        CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (!ProcessUtilities.IsAlive(pid))
            {
                throw new InstanceOperationException($"p4d exited after starting. {ReadLogTail(logPath)}".Trim());
            }

            try
            {
                using TcpClient client = new();
                await client.ConnectAsync(IPAddress.Loopback, instance.Port, cancellationToken);
                return;
            }
            catch (SocketException) when (elapsed.Elapsed < StartTimeout)
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (SocketException)
            {
                throw new InstanceOperationException(
                    $"p4d does not accept connections on port {instance.Port}. {ReadLogTail(logPath)}".Trim());
            }
        }
    }

    private static bool TryReadPid(string pidFile, out int pid)
    {
        pid = 0;
        try
        {
            return File.Exists(pidFile)
                && int.TryParse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture, out pid)
                && pid > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string ReadLogTail(string logPath)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return string.Empty;
            }
            string[] lines = File.ReadAllLines(logPath);
            return string.Join(Environment.NewLine, lines.TakeLast(10).Where(l => !string.IsNullOrWhiteSpace(l)));
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
