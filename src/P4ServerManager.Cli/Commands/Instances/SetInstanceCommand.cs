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
        Option<string?> nameOption = new("--name")
        {
            Description = "New unique name: a letter, then up to 31 letters, digits, '-' or '_'. The p4d server keeps "
                + "its server ID and the prefix of its journals: 'instance server-id' changes the server ID.",
        };
        Option<string?> descriptionOption = new("--description")
        {
            Description = "Free text. An empty text removes the description.",
        };
        Option<bool?> autoStartOption = new("--auto-start")
        {
            Description = "Whether the daemon starts p4d when it starts: true or false.",
            HelpName = "true|false",
        };
        Command command = new("set", "Change the settings of an instance that can change after its creation.")
        {
            instanceArgument,
            nameOption,
            descriptionOption,
            autoStartOption,
        };
        command.Validators.Add(result =>
        {
            if (result.GetResult(nameOption) is null
                && result.GetResult(descriptionOption) is null
                && result.GetResult(autoStartOption) is null)
            {
                result.AddError("Give at least one setting to change: --name, --description or --auto-start.");
            }
        });

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);

            UpdateInstanceRequest request = new()
            {
                Name = invocation.GetValue(nameOption),
                Description = invocation.GetValue(descriptionOption),
                AutoStart = invocation.GetValue(autoStartOption),
            };
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
