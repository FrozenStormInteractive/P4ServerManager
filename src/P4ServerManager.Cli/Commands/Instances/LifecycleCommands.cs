using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

/// <summary>
/// The start, the stop and the restart of the p4d of an instance. The daemon answers once they are done.
/// </summary>
internal static class LifecycleCommands
{
    public static IEnumerable<Command> Create(CliServices services)
    {
        yield return Create(
            services,
            "start",
            "Start the p4d of an instance and wait until it accepts connections.",
            "Starting",
            "Started",
            (client, id, cancellationToken) => client.StartInstanceAsync(id, cancellationToken));
        yield return Create(
            services,
            "stop",
            "Stop the p4d of an instance and wait until it exits.",
            "Stopping",
            "Stopped",
            (client, id, cancellationToken) => client.StopInstanceAsync(id, cancellationToken));
        yield return Create(
            services,
            "restart",
            "Stop the p4d of an instance if it runs, then start it again.",
            "Restarting",
            "Restarted",
            (client, id, cancellationToken) => client.RestartInstanceAsync(id, cancellationToken));
    }

    private static Command Create(
        CliServices services,
        string name,
        string description,
        string progressVerb,
        string doneVerb,
        Func<DaemonClient, Guid, CancellationToken, Task<InstanceDto>> operation)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Command command = new(name, description) { instanceArgument };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);

            await invocation.Progress.WriteLineAsync($"{progressVerb} {instance.Name}...");
            InstanceDto result = await operation(client, instance.Id, cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(result, CliJsonContext.Default.InstanceDto);
            }
            else
            {
                await invocation.Output.WriteLineAsync($"{doneVerb} {result.Name}.");
            }
            return 0;
        });
        return command;
    }
}
