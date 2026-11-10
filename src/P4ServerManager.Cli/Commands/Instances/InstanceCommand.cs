using System.CommandLine;

namespace P4ServerManager.Cli.Commands.Instances;

/// <summary>
/// The <c>instance</c> command and its subcommands.
/// </summary>
internal static class InstanceCommand
{
    public static Command Create(CliServices services)
    {
        Command command = new("instance", "Manage the p4d servers.");
        command.Aliases.Add("instances");
        command.Subcommands.Add(ListInstancesCommand.Create(services));
        command.Subcommands.Add(ShowInstanceCommand.Create(services));
        command.Subcommands.Add(CreateInstanceCommand.Create(services));
        foreach (Command lifecycleCommand in LifecycleCommands.Create(services))
        {
            command.Subcommands.Add(lifecycleCommand);
        }
        command.Subcommands.Add(MaintenanceModeCommand.Create(services));
        command.Subcommands.Add(SetInstanceCommand.Create(services));
        command.Subcommands.Add(SuperUserCommand.Create(services));
        command.Subcommands.Add(UpgradeInstanceCommand.Create(services));
        command.Subcommands.Add(ConvertInstanceToUnicodeCommand.Create(services));
        command.Subcommands.Add(ChangeServerIdCommand.Create(services));
        command.Subcommands.Add(InstanceHealthCommand.Create(services));
        command.Subcommands.Add(DeleteInstanceCommand.Create(services));
        command.Subcommands.Add(InstanceOperationCommand.Create(services));
        return command;
    }
}
