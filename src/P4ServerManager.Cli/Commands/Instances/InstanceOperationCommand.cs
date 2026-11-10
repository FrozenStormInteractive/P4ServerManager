using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Operations;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class InstanceOperationCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Option<bool> followOption = new("--follow", "-f") { Description = "Follow the operation until it ends." };
        Command command = new("operation", "Show the last long operation on an instance, such as its provisioning or its upgrade.")
        {
            instanceArgument,
            followOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);

            OperationSnapshot snapshot;
            if (invocation.GetValue(followOption))
            {
                snapshot = await OperationWaiter.WaitAsync(invocation, client, instance.Id, cancellationToken)
                    ?? throw NoOperation(instance);
            }
            else
            {
                snapshot = await client.GetOperationAsync(instance.Id, cancellationToken) ?? throw NoOperation(instance);
                if (!invocation.Json)
                {
                    await invocation.Output.WriteLineAsync($"{snapshot.Kind}: {snapshot.Status}");
                    for (int index = 0; index < snapshot.Steps.Count; index++)
                    {
                        OperationStepState step = snapshot.Steps[index];
                        int? percent = index == snapshot.CurrentStepIndex ? snapshot.Percent : null;
                        await invocation.Output.WriteLineAsync(OperationProgressWriter.FormatLine(step, percent));
                    }
                }
            }

            if (invocation.Json)
            {
                invocation.WriteJson(snapshot, CliJsonContext.Default.OperationSnapshot);
            }
            return await OperationWaiter.ReportEndAsync(invocation, snapshot);
        });
        return command;
    }

    private static CliException NoOperation(InstanceDto instance)
    {
        return new CliException($"The daemon has run no operation on {instance.Name} since it started.");
    }
}
