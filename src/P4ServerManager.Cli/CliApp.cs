using System.CommandLine;
using System.CommandLine.Help;

using P4ServerManager.Cli.Commands.Instances;

namespace P4ServerManager.Cli;

internal static class CliApp
{
    /// <summary>
    /// Name the CLI is installed under. The help shows it instead of the name of the executable.
    /// </summary>
    public const string Name = "p4sm";

    public static Command BuildRootCommand(CliEnvironment environment)
    {
        GlobalOptions globalOptions = new();
        CliServices services = new(environment, globalOptions);

        Command root = new(
            Name,
            "P4 Server Manager CLI")
        {
            new HelpOption(),
            new VersionOption(),
            globalOptions.Socket,
            globalOptions.Json,
        };
        root.Subcommands.Add(InstanceCommand.Create(services));
        return root;
    }
}
