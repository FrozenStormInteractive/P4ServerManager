using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

internal static class ProcessRunnerExtensions
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs a command, and throws an <see cref="InstanceOperationException"/> when it fails.
    /// </summary>
    public static async Task<ProcessResult> RunCheckedAsync(
        this IProcessRunner processRunner,
        ProcessCommand command,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(command, cancellationToken);
        if (!result.Succeeded)
        {
            // The arguments never hold the password: it goes through the standard input or the environment.
            throw new InstanceOperationException(
                $"'{Path.GetFileName(command.FileName)} {string.Join(' ', command.Arguments)}' failed: {result.ErrorText}");
        }
        return result;
    }
}
