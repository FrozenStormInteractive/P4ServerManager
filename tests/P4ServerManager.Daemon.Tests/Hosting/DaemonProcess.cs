using System.Diagnostics;
using System.Text;

namespace P4ServerManager.Daemon.Tests.Hosting;

/// <summary>
/// The daemon built next to the tests, run in its own process on an empty data directory. Killed when disposed.
/// </summary>
internal sealed class DaemonProcess : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly StringBuilder _output = new();

    private DaemonProcess(Process process)
    {
        _process = process;
    }

    public static DaemonProcess Start(string dataDirectory)
    {
        string executable = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "P4ServerManager.Daemon.exe" : "P4ServerManager.Daemon");
        ProcessStartInfo startInfo = new(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--environment");
        startInfo.ArgumentList.Add("Testing");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:0");
        startInfo.ArgumentList.Add($"--Daemon:DataDirectory={dataDirectory}");

        DaemonProcess daemon = new(Process.Start(startInfo)!);
        daemon._process.OutputDataReceived += daemon.OnOutput;
        daemon._process.ErrorDataReceived += daemon.OnOutput;
        daemon._process.BeginOutputReadLine();
        daemon._process.BeginErrorReadLine();
        return daemon;
    }

    /// <summary>
    /// Sends the request until the daemon listens on its socket.
    /// </summary>
    public async Task<HttpResponseMessage> WaitForSocketAsync(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return await send();
            }
            catch (HttpRequestException) when (!_process.HasExited && elapsed.Elapsed < StartTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new InvalidOperationException($"The daemon did not answer on its socket. Its output:\n{Output}", exception);
            }
        }
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }
        _process.Dispose();
    }

    private string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        lock (_output)
        {
            _output.AppendLine(e.Data);
        }
    }
}
