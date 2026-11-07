using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Operations;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class ConvertInstanceToUnicodeCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Option<bool> yesOption = new("--yes", "-y") { Description = "Convert without asking for a confirmation." };
        Option<bool> noWaitOption = new("--no-wait")
        {
            Description = "Return once the conversion has begun, without waiting for its end.",
        };
        Command command = new(
            "convert-to-unicode",
            "Convert an instance to Unicode mode: stop it, save a checkpoint, switch the database, then start it again "
            + "if it ran. It cannot be undone.")
        {
            instanceArgument,
            yesOption,
            noWaitOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);

            if (!invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException(
                        "Converting an instance to Unicode mode asks for a confirmation. Without a terminal, pass --yes.");
                }
                string question = $"Convert {instance.Name} to Unicode mode? Its users are disconnected until it "
                    + "starts again, clients then need a character set (P4CHARSET), and it cannot be undone.";
                if (!prompter.Confirm(question))
                {
                    await invocation.Error.WriteLineAsync("Nothing was converted.");
                    return CliServices.ErrorExitCode;
                }
            }

            InstanceDto converting = await client.ConvertInstanceToUnicodeAsync(instance.Id, cancellationToken);

            if (invocation.GetValue(noWaitOption))
            {
                if (invocation.Json)
                {
                    invocation.WriteJson(converting, CliJsonContext.Default.InstanceDto);
                }
                else
                {
                    await invocation.Output.WriteLineAsync(
                        $"Converting {converting.Name}. The conversion goes on in the daemon: follow it with "
                        + $"'p4sm instance operation {converting.Name} --follow'.");
                }
                return 0;
            }

            await invocation.Progress.WriteLineAsync($"Converting {converting.Name} to Unicode mode...");
            OperationSnapshot? snapshot;
            try
            {
                snapshot = await OperationWaiter.WaitAsync(invocation, client, converting.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await invocation.Error.WriteLineAsync(OperationWaiter.StoppedWaitingMessage(converting.Name));
                return CliServices.CanceledExitCode;
            }

            int exitCode = await OperationWaiter.ReportEndAsync(invocation, snapshot);
            InstanceDto converted = await client.GetInstanceAsync(converting.Id, cancellationToken);
            if (invocation.Json)
            {
                invocation.WriteJson(converted, CliJsonContext.Default.InstanceDto);
            }
            else if (exitCode == 0)
            {
                await invocation.Output.WriteLineAsync($"{converted.Name} now runs in Unicode mode.");
            }
            else if (converted.LastError is { } lastError && lastError != snapshot?.Error)
            {
                // Tells, for example, that the server could not be started again.
                await invocation.Error.WriteLineAsync(lastError);
            }
            return exitCode;
        });
        return command;
    }
}
