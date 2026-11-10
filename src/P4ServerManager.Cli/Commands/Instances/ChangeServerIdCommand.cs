using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Operations;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class ChangeServerIdCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Argument<string> serverIdArgument = new("server-id")
        {
            Description = "New server ID: a letter, then up to 31 letters, digits, '-' or '_'.",
        };
        Option<bool> yesOption = new("--yes", "-y") { Description = "Change it without asking for a confirmation." };
        Option<bool> noWaitOption = new("--no-wait")
        {
            Description = "Return once the change has begun, without waiting for its end.",
        };
        Command command = new(
            "server-id",
            "Change the p4d server ID of an instance, and rename its server spec as the stored super user. A running "
            + "server is restarted; a stopped one is started to rename the spec, then stopped again. The name of the "
            + "instance and the prefix of its journals stay the same.")
        {
            instanceArgument,
            serverIdArgument,
            yesOption,
            noWaitOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);
            string serverId = invocation.GetValue(serverIdArgument)!;

            if (!invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException(
                        "Changing the server ID asks for a confirmation. Without a terminal, pass --yes.");
                }
                string question = $"Change the server ID of {instance.Name} from {instance.ServerId ?? "unknown"} to "
                    + $"{serverId}? Its users are disconnected while the server restarts.";
                if (!prompter.Confirm(question))
                {
                    await invocation.Error.WriteLineAsync("Nothing was changed.");
                    return CliServices.ErrorExitCode;
                }
            }

            InstanceDto changing = await client.ChangeServerIdAsync(
                instance.Id,
                new ChangeServerIdRequest { ServerId = serverId },
                cancellationToken);

            if (invocation.GetValue(noWaitOption))
            {
                if (invocation.Json)
                {
                    invocation.WriteJson(changing, CliJsonContext.Default.InstanceDto);
                }
                else
                {
                    await invocation.Output.WriteLineAsync(
                        $"Changing the server ID of {changing.Name}. The change goes on in the daemon: follow it with "
                        + $"'p4sm instance operation {changing.Name} --follow'.");
                }
                return 0;
            }

            await invocation.Progress.WriteLineAsync($"Changing the server ID of {changing.Name} to {serverId}...");
            OperationSnapshot? snapshot;
            try
            {
                snapshot = await OperationWaiter.WaitAsync(invocation, client, changing.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await invocation.Error.WriteLineAsync(OperationWaiter.StoppedWaitingMessage(changing.Name));
                return CliServices.CanceledExitCode;
            }

            int exitCode = await OperationWaiter.ReportEndAsync(invocation, snapshot);
            InstanceDto changed = await client.GetInstanceAsync(changing.Id, cancellationToken);
            if (invocation.Json)
            {
                invocation.WriteJson(changed, CliJsonContext.Default.InstanceDto);
            }
            else if (exitCode == 0)
            {
                await invocation.Output.WriteLineAsync($"{changed.Name} now has server ID {changed.ServerId}.");
            }
            else if (changed.LastError is { } lastError && lastError != snapshot?.Error)
            {
                // Tells, for example, that the server could not be started again.
                await invocation.Error.WriteLineAsync(lastError);
            }
            return exitCode;
        });
        return command;
    }
}
