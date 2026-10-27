using P4ServerManager.Cli.Api;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Operations;

/// <summary>
/// Follows a long operation of the daemon, such as a provisioning or an upgrade, until it ends.
/// </summary>
internal static class OperationWaiter
{
    /// <summary>
    /// Reads the operation of the instance until it ends, and writes its progress.
    /// </summary>
    /// <returns>
    /// The last snapshot, or null when the daemon has no operation for the instance and the instance is no longer being
    /// provisioned.
    /// </returns>
    public static async Task<OperationSnapshot?> WaitAsync(
        CliInvocation invocation,
        DaemonClient client,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        OperationProgressWriter progress = new(
            invocation.Progress,
            rewriteInPlace: invocation.Environment.OutputIsTerminal && !invocation.Json);
        try
        {
            while (true)
            {
                OperationSnapshot? snapshot = await client.GetOperationAsync(instanceId, cancellationToken);
                if (snapshot is not null)
                {
                    progress.Report(snapshot);
                    if (snapshot.Status != OperationStatus.Running)
                    {
                        return snapshot;
                    }
                }
                // Right after its creation, an instance waits for the provisioning worker: no operation is known yet.
                else if ((await client.GetInstanceAsync(instanceId, cancellationToken)).State
                    != P4ServerInstanceState.Provisioning)
                {
                    return null;
                }

                await Task.Delay(invocation.Environment.PollInterval, cancellationToken);
            }
        }
        finally
        {
            progress.Complete();
        }
    }

    /// <summary>
    /// The exit code and the message of an operation that ended.
    /// </summary>
    public static async Task<int> ReportEndAsync(CliInvocation invocation, OperationSnapshot? snapshot)
    {
        if (snapshot?.Status == OperationStatus.Failed)
        {
            await invocation.Error.WriteLineAsync($"Error: {snapshot.Error ?? "The operation failed."}");
            return CliServices.ErrorExitCode;
        }
        return 0;
    }

    public static string StoppedWaitingMessage(string instanceName)
    {
        return "Stopped waiting. The operation goes on in the daemon: follow it with "
            + $"'p4sm instance operation {instanceName} --follow'.";
    }
}
