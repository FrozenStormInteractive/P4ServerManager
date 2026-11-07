using System.Diagnostics;
using System.Text;

namespace P4ServerManager.Daemon.Services.Processes;

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(command.FileName)
        {
            RedirectStandardInput = command.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (command.Environment is not null)
        {
            foreach ((string name, string value) in command.Environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        logger.LogDebug("Running {FileName} {Arguments}", command.FileName, string.Join(' ', command.Arguments));

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {command.FileName}.");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(command.Timeout ?? DefaultTimeout);

        try
        {
            Task<string> standardOutput = ReadAsync(process.StandardOutput, command.OnOutputLine, timeout.Token);
            Task<string> standardError = ReadAsync(process.StandardError, command.OnErrorLine, timeout.Token);

            if (command.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(command.StandardInput.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException($"{Path.GetFileName(command.FileName)} did not finish in time.");
        }
    }

    private static async Task<string> ReadAsync(
        StreamReader reader,
        Func<string, Task>? onLine,
        CancellationToken cancellationToken)
    {
        if (onLine is null)
        {
            return await reader.ReadToEndAsync(cancellationToken);
        }

        StringBuilder text = new();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            text.AppendLine(line);
            await onLine(line);
        }
        return text.ToString();
    }
}

/// <summary>
/// Runs short-lived commands (p4d -V, p4d -xu, p4 ...) and captures their output.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken);
}

/// <param name="FileName">Executable to run.</param>
/// <param name="Arguments">Arguments, passed one by one without shell quoting.</param>
/// <param name="StandardInput">Text written to the standard input. It is never logged: it may hold a password.</param>
/// <param name="Environment">Variables added to the environment of the process.</param>
/// <param name="Timeout">Time after which the process is killed.</param>
/// <param name="OnOutputLine">
/// Called with each line of the standard output as it comes. Both handlers may run at the same time.
/// </param>
/// <param name="OnErrorLine">Called with each line of the error output as it comes.</param>
public sealed record ProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StandardInput = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    TimeSpan? Timeout = null,
    Func<string, Task>? OnOutputLine = null,
    Func<string, Task>? OnErrorLine = null);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// Error output, or the standard output when the error output is empty.
    /// </summary>
    public string ErrorText => string.IsNullOrWhiteSpace(StandardError) ? StandardOutput.Trim() : StandardError.Trim();
}
