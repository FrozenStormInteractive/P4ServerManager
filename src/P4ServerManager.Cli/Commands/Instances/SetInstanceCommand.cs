using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Output;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class SetInstanceCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Option<bool?> autoStartOption = new("--auto-start")
        {
            Description = "Whether the daemon starts p4d when it starts: true or false.",
            HelpName = "true|false",
        };
        Command command = new("set", "Change the settings of an instance that can change after its creation.")
        {
            instanceArgument,
            autoStartOption,
        };
        command.Validators.Add(result =>
        {
            if (result.GetResult(autoStartOption) is null)
            {
                result.AddError("Give at least one setting to change, such as --auto-start.");
            }
        });

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);

            UpdateInstanceRequest request = new() { AutoStart = invocation.GetValue(autoStartOption) };
            InstanceDto updated = await client.UpdateInstanceAsync(instance.Id, request, cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(updated, CliJsonContext.Default.InstanceDto);
            }
            else
            {
                await invocation.Output.WriteLineAsync(
                    $"Updated {updated.Name}. Auto-start: {InstanceFormatting.FormatYesNo(updated.AutoStart)}.");
            }
            return 0;
        });
        return command;
    }
}
