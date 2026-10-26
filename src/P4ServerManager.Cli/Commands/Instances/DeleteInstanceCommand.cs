using System.CommandLine;

using P4ServerManager.Cli.Api;
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
            Description = "Also delete its logs and, when the daemon chose it, its root with the depots. A root given "
                + "at creation is always kept.",
        };
        Option<bool> yesOption = new("--yes", "-y") { Description = "Delete without asking for a confirmation." };
        Command command = new("delete", "Stop the p4d of an instance if it runs, then remove the instance.")
        {
            instanceArgument,
            deleteFilesOption,
            yesOption,
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
                    ? $"Delete the instance {instance.Name} and its files?"
                    : $"Delete the instance {instance.Name}? Its files are kept.";
                if (!prompter.Confirm(question))
                {
                    await invocation.Error.WriteLineAsync("Nothing was deleted.");
                    return CliServices.ErrorExitCode;
                }
            }

            await invocation.Progress.WriteLineAsync($"Deleting {instance.Name}...");
            await client.DeleteInstanceAsync(instance.Id, deleteFiles, cancellationToken);
            if (!invocation.Json)
            {
                await invocation.Output.WriteLineAsync($"Deleted {instance.Name}.");
            }
            return 0;
        });
        return command;
    }
}
