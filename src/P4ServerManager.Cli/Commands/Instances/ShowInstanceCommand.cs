using System.CommandLine;
using System.Globalization;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Output;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class ShowInstanceCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Command command = new("show", "Show the settings and the status of an instance.") { instanceArgument };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(instance, CliJsonContext.Default.InstanceDto);
                return 0;
            }

            List<(string Label, string? Value)> fields =
            [
                ("Name", instance.Name),
                ("Id", instance.Id.ToString()),
                ("Description", instance.Description),
                ("State", instance.State.ToString()),
                ("Status", instance.RuntimeStatus.ToString()),
                ("Port", InstanceFormatting.FormatPort(instance)),
                ("SSL fingerprint", instance.SslFingerprint),
                ("Root", instance.RootPath),
                ("Release", instance.Release),
                ("Version", instance.Version is null ? null : InstanceFormatting.FormatVersion(instance)),
                ("Case sensitive", InstanceFormatting.FormatYesNo(instance.CaseSensitive)),
                ("Unicode", InstanceFormatting.FormatYesNo(instance.Unicode)),
                ("Auto-start", InstanceFormatting.FormatYesNo(instance.AutoStart)),
                ("Created", instance.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
                ("Last error", instance.LastError),
            ];
            int width = fields.Max(f => f.Label.Length) + 2;
            foreach ((string label, string? value) in fields.Where(f => !string.IsNullOrEmpty(f.Value)))
            {
                await invocation.Output.WriteLineAsync($"{(label + ":").PadRight(width)}{value}");
            }
            return 0;
        });
        return command;
    }
}
