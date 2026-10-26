using System.CommandLine;

using P4ServerManager.Cli.Api;

namespace P4ServerManager.Cli;

/// <summary>
/// What the commands share, given to each of them when the command tree is built.
/// </summary>
internal sealed class CliServices(CliEnvironment environment, GlobalOptions globalOptions)
{
    /// <summary>
    /// Exit code of a command that failed.
    /// </summary>
    public const int ErrorExitCode = 1;

    /// <summary>
    /// Exit code of a command stopped by Ctrl+C, as shells report a process ended by SIGINT.
    /// </summary>
    public const int CanceledExitCode = 130;

    public GlobalOptions GlobalOptions { get; } = globalOptions;

    /// <summary>
    /// Sets the action of a command. The errors of the CLI and of the daemon are written with their message alone and
    /// end the command with <see cref="ErrorExitCode"/>.
    /// </summary>
    public void SetAction(Command command, Func<CliInvocation, CancellationToken, Task<int>> action)
    {
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            CliInvocation invocation = new(parseResult, environment, GlobalOptions);
            try
            {
                return await action(invocation, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CanceledExitCode;
            }
            catch (CliException exception)
            {
                await invocation.Error.WriteLineAsync($"Error: {exception.Message}");
            }
            catch (DaemonApiException exception)
            {
                await invocation.Error.WriteLineAsync($"Error: {exception.Message}");
                foreach ((string field, string[] messages) in exception.Problem?.Errors ?? new Dictionary<string, string[]>())
                {
                    foreach (string message in messages)
                    {
                        await invocation.Error.WriteLineAsync($"  {field}: {message}");
                    }
                }
            }
            return ErrorExitCode;
        });
    }
}
