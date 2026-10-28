using System.CommandLine;

namespace P4ServerManager.Cli.Commands.Builds;

/// <summary>
/// The <c>build</c> command and its subcommands.
/// </summary>
internal static class BuildCommand
{
    public static Command Create(CliServices services)
    {
        Command command = new("build", "Manage the archive of p4d builds.");
        command.Aliases.Add("builds");
        command.Subcommands.Add(ListBuildsCommand.Create(services));
        command.Subcommands.Add(ImportBuildCommand.Create(services));
        command.Subcommands.Add(DownloadBuildCommand.Create(services));
        foreach (Command protectCommand in ProtectBuildCommands.Create(services))
        {
            command.Subcommands.Add(protectCommand);
        }
        command.Subcommands.Add(DeleteBuildCommand.Create(services));
        command.Subcommands.Add(ArchiveCommands.CreateCheck(services));
        command.Subcommands.Add(ArchiveCommands.CreateStatus(services));
        return command;
    }
}
