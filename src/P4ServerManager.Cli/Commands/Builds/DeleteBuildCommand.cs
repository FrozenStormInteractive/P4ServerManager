using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Output;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Builds;

internal static class DeleteBuildCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> buildArgument = BuildArgument.Create();
        Option<bool> yesOption = new("--yes", "-y") { Description = "Delete without asking for a confirmation." };
        Command command = new(
            "delete",
            "Remove a build from the archive, with its extracted files. A build that an instance uses is kept.")
        {
            buildArgument,
            yesOption,
        };
        command.Aliases.Add("rm");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            BuildDto build = await BuildArgument.ResolveAsync(
                client,
                invocation.GetValue(buildArgument),
                cancellationToken);
            string version = BuildFormatting.FormatVersion(build);

            if (!invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException("Deleting a build asks for a confirmation. Without a terminal, pass --yes.");
                }
                string question = build.Source == BuildSource.Ftp
                    ? $"Delete p4d {version}? Perforce may no longer offer it."
                    : $"Delete p4d {version}? It was imported: only a new import brings it back.";
                if (!prompter.Confirm(question))
                {
                    await invocation.Error.WriteLineAsync("Nothing was deleted.");
                    return CliServices.ErrorExitCode;
                }
            }

            await client.DeleteBuildAsync(build.Id, cancellationToken);
            if (!invocation.Json)
            {
                await invocation.Output.WriteLineAsync($"Deleted p4d {version}.");
            }
            return 0;
        });
        return command;
    }
}
