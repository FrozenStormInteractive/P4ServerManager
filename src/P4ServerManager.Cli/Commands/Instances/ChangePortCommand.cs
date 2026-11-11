using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Operations;
using P4ServerManager.Cli.Output;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class ChangePortCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Argument<int> portArgument = new("port")
        {
            Description = "New TCP port, between 1024 and 65535.",
        };
        Option<bool> yesOption = new("--yes", "-y") { Description = "Change it without asking for a confirmation." };
        Option<bool> noWaitOption = new("--no-wait")
        {
            Description = "Return once the change has begun, without waiting for its end.",
        };
        Command command = new(
            "port",
            "Move the p4d of an instance to another port, and update the address of its server spec as the stored "
            + "super user. A running server is restarted on the new port; a stopped one is started to update the spec, "
            + "then stopped again. Without a usable super user, the spec is left as it is.")
        {
            instanceArgument,
            portArgument,
            yesOption,
            noWaitOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);
            int port = invocation.GetValue(portArgument);

            if (!invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException(
                        "Changing the port asks for a confirmation. Without a terminal, pass --yes.");
                }
                string question = $"Move {instance.Name} from port {instance.Port} to {port}? Its users are "
                    + "disconnected while the server restarts, and must then connect to the new port.";
                if (!prompter.Confirm(question))
                {
                    await invocation.Error.WriteLineAsync("Nothing was changed.");
                    return CliServices.ErrorExitCode;
                }
            }

            InstanceDto changing = await client.ChangePortAsync(
                instance.Id,
                new ChangePortRequest { Port = port },
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
                        $"Changing the port of {changing.Name}. The change goes on in the daemon: follow it with "
                        + $"'p4sm instance operation {changing.Name} --follow'.");
                }
                return 0;
            }

            await invocation.Progress.WriteLineAsync($"Moving {changing.Name} to port {port}...");
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
                await invocation.Output.WriteLineAsync(
                    $"{changed.Name} now listens on port {InstanceFormatting.FormatPort(changed)}.");
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
