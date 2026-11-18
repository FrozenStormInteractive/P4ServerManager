using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class DeleteInstanceCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Option<bool> deleteFilesOption = new("--delete-files")
        {
            Description = "Also delete the files of its server, wherever its server directory is: its root, depots, "
                + "checkpoints, journals and logs.",
        };
        Option<bool> yesOption = new("--yes", "-y") { Description = "Delete without asking for a confirmation." };
        Option<bool> noWaitOption = new("--no-wait")
        {
            Description = "Return once the deletion has begun, without waiting for its end.",
        };
        Command command = new(
            "delete",
            "Stop the p4d of an instance if it runs, then remove the instance with what the daemon keeps for it. The "
            + "files of its server are kept unless --delete-files is given.")
        {
            instanceArgument,
            deleteFilesOption,
            yesOption,
            noWaitOption,
        };
        command.Aliases.Add("rm");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);
            bool deleteFiles = invocation.GetValue(deleteFilesOption);

            if (!invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException(
                        "Deleting an instance asks for a confirmation. Without a terminal, pass --yes.");
                }
                string question = deleteFiles
                    ? $"Delete the instance {instance.Name} and the files of its server: its root, depots, "
                        + "checkpoints, journals and logs?"
                    : $"Delete the instance {instance.Name}? The files of its server are kept.";
                if (!prompter.Confirm(question))
                {
                    await invocation.Error.WriteLineAsync("Nothing was deleted.");
                    return CliServices.ErrorExitCode;
                }
            }

            InstanceDto deleting = await client.DeleteInstanceAsync(instance.Id, deleteFiles, cancellationToken);
            if (invocation.GetValue(noWaitOption))
            {
                if (invocation.Json)
                {
                    invocation.WriteJson(deleting, CliJsonContext.Default.InstanceDto);
                }
                else
                {
                    await invocation.Output.WriteLineAsync(
                        $"Deleting {instance.Name}. The deletion goes on in the daemon: "
                        + $"'p4sm instance show {instance.Name}' fails once it is done.");
                }
                return 0;
            }

            await invocation.Progress.WriteLineAsync($"Deleting {instance.Name}...");
            InstanceDto? left;
            try
            {
                left = await WaitAsync(invocation, client, instance.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await invocation.Error.WriteLineAsync("Stopped waiting. The deletion goes on in the daemon.");
                return CliServices.ErrorExitCode;
            }

            if (left is not null)
            {
                await invocation.Error.WriteLineAsync(
                    $"Error: {left.LastError ?? $"{instance.Name} could not be deleted."}");
                return CliServices.ErrorExitCode;
            }
            if (!invocation.Json)
            {
                await invocation.Output.WriteLineAsync($"Deleted {instance.Name}.");
            }
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Reads the instance until it is gone, or no longer being deleted.
    /// </summary>
    /// <returns>Null once the instance is deleted, otherwise the instance the failed deletion left.</returns>
    private static async Task<InstanceDto?> WaitAsync(
        CliInvocation invocation,
        DaemonClient client,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            InstanceDto? instance = await client.FindInstanceAsync(instanceId, cancellationToken);
            if (instance is null || instance.RuntimeStatus != InstanceRuntimeStatus.Deleting)
            {
                return instance;
            }
            await Task.Delay(invocation.Environment.PollInterval, cancellationToken);
        }
    }

}
