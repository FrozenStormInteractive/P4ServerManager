using System.CommandLine;
using System.Globalization;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Output;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class ListInstancesCommand
{
    public static Command Create(CliServices services)
    {
        Command command = new("list", "List the instances.");
        command.Aliases.Add("ls");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            IReadOnlyList<InstanceDto> instances = await client.GetInstancesAsync(cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(instances, CliJsonContext.Default.IReadOnlyListInstanceDto);
                return 0;
            }
            if (instances.Count == 0)
            {
                await invocation.Output.WriteLineAsync("No instances. Create one with 'p4sm instance create'.");
                return 0;
            }

            TableWriter table = new("NAME", "STATE", "STATUS", "PORT", "VERSION", "AUTO-START");
            foreach (InstanceDto instance in instances)
            {
                table.AddRow(
                    instance.Name,
                    instance.State.ToString(),
                    instance.RuntimeStatus.ToString(),
                    instance.Port.ToString(CultureInfo.InvariantCulture),
                    InstanceFormatting.FormatVersion(instance),
                    InstanceFormatting.FormatYesNo(instance.AutoStart));
            }
            table.Write(invocation.Output);
            return 0;
        });
        return command;
    }
}
