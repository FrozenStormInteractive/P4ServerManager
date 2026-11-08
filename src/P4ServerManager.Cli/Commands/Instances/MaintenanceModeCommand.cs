using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

/// <summary>
/// The <c>maintenance</c> command: enables or disables the maintenance mode (<c>p4d -n</c>) of an instance.
/// </summary>
internal static class MaintenanceModeCommand
{
    public static Command Create(CliServices services)
    {
        Command command = new(
            "maintenance",
            "Enable or disable the maintenance mode of an instance. In maintenance mode, p4d only allows the "
            + "commands that do not need a client workspace: users can no longer sync or submit.");
        command.Subcommands.Add(CreateEnable(services));
        command.Subcommands.Add(CreateDisable(services));
        return command;
    }

    private static Command CreateEnable(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Command command = new(
            "enable",
            "Enable the maintenance mode of an instance: restart its p4d with -n if it runs, start it with -n if it is "
            + "stopped. Every later start of p4d stays in maintenance mode, until 'p4sm instance maintenance disable'.")
        {
            instanceArgument,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);

            bool running = instance.RuntimeStatus == InstanceRuntimeStatus.Running;
            await invocation.Progress.WriteLineAsync(running
                ? $"Restarting {instance.Name} in maintenance mode..."
                : $"Starting {instance.Name} in maintenance mode...");
            InstanceDto updated = await client.EnableMaintenanceModeAsync(instance.Id, cancellationToken);
            return await WriteAsync(invocation, updated, $"{updated.Name} runs in maintenance mode.");
        });
        return command;
    }

    private static Command CreateDisable(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Command command = new(
            "disable",
            "Disable the maintenance mode of an instance: restart its p4d in normal mode if it runs. A stopped p4d stays "
            + "stopped.")
        {
            instanceArgument,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);

            if (instance.MaintenanceMode && instance.RuntimeStatus == InstanceRuntimeStatus.Running)
            {
                await invocation.Progress.WriteLineAsync($"Restarting {instance.Name} in normal mode...");
            }
            InstanceDto updated = await client.DisableMaintenanceModeAsync(instance.Id, cancellationToken);
            return await WriteAsync(
                invocation,
                updated,
                updated.RuntimeStatus == InstanceRuntimeStatus.Running
                    ? $"{updated.Name} runs in normal mode."
                    : $"The maintenance mode of {updated.Name} is disabled: its next start is a normal one.");
        });
        return command;
    }

    private static async Task<int> WriteAsync(CliInvocation invocation, InstanceDto instance, string message)
    {
        if (invocation.Json)
        {
            invocation.WriteJson(instance, CliJsonContext.Default.InstanceDto);
        }
        else
        {
            await invocation.Output.WriteLineAsync(message);
        }
        return 0;
    }
}
