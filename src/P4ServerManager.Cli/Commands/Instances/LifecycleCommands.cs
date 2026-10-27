using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

/// <summary>
/// The start, the stop and the restart of the p4d of one or more instances, or of all of them. The daemon runs them in
/// parallel and answers once they are all done.
/// </summary>
internal static class LifecycleCommands
{
    public static IEnumerable<Command> Create(CliServices services)
    {
        yield return Create(
            services,
            new LifecycleAction(
                "start",
                "Start the p4d of instances and wait until they accept connections. Those already running are skipped.",
                "Starting",
                "Started",
                "already runs"));
        yield return Create(
            services,
            new LifecycleAction(
                "stop",
                "Stop the p4d of instances and wait until they exit. Those already stopped are skipped.",
                "Stopping",
                "Stopped",
                "is already stopped"));
        yield return Create(
            services,
            new LifecycleAction(
                "restart",
                "Stop the p4d of instances if they run, then start them again. With --all, only the running ones are "
                + "restarted.",
                "Restarting",
                "Restarted",
                "is stopped: not restarted"));
    }

    private static Command Create(CliServices services, LifecycleAction action)
    {
        Argument<string[]> instancesArgument = InstanceArgument.CreateMany();
        Option<bool> allOption = new("--all", "-a") { Description = "Every ready instance, instead of a list." };
        Command command = new(action.Name, action.Description) { instancesArgument, allOption };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            string[] references = invocation.GetValue(instancesArgument) ?? [];
            bool all = invocation.GetValue(allOption);
            if (all == references.Length > 0)
            {
                throw new CliException(all
                    ? "Name instances or pass --all, not both."
                    : "Name at least one instance, or pass --all.");
            }

            using DaemonClient client = invocation.CreateClient();
            InstanceSelectionRequest selection;
            if (all)
            {
                selection = new InstanceSelectionRequest { All = true };
                await invocation.Progress.WriteLineAsync($"{action.ProgressVerb} all the instances...");
            }
            else
            {
                IReadOnlyList<InstanceDto> instances = await client.ResolveInstancesAsync(references, cancellationToken);
                selection = new InstanceSelectionRequest { InstanceIds = instances.Select(i => i.Id).ToList() };
                await invocation.Progress.WriteLineAsync(
                    $"{action.ProgressVerb} {string.Join(", ", instances.Select(i => i.Name))}...");
            }

            IReadOnlyList<InstanceActionResult> results = await client.RunInstancesActionAsync(
                action.Name,
                selection,
                cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(results, CliJsonContext.Default.IReadOnlyListInstanceActionResult);
            }
            else if (results.Count == 0)
            {
                await invocation.Output.WriteLineAsync("No instance is ready.");
            }
            foreach (InstanceActionResult result in results)
            {
                string name = result.Instance.Name;
                switch (result.Outcome)
                {
                    case InstanceActionOutcome.Failed:
                        await invocation.Error.WriteLineAsync($"Error: {name}: {result.Error}");
                        break;
                    case InstanceActionOutcome.Skipped when !invocation.Json:
                        await invocation.Output.WriteLineAsync($"{name} {action.SkippedText}.");
                        break;
                    case InstanceActionOutcome.Succeeded when !invocation.Json:
                        await invocation.Output.WriteLineAsync($"{action.DoneVerb} {name}.");
                        break;
                }
            }
            return results.Any(r => r.Outcome == InstanceActionOutcome.Failed) ? CliServices.ErrorExitCode : 0;
        });
        return command;
    }

    /// <param name="Name">Name of the command, and of the action in the API.</param>
    /// <param name="Description">Help of the command.</param>
    /// <param name="ProgressVerb">Written while the action runs, such as <c>Starting</c>.</param>
    /// <param name="DoneVerb">Written for each instance it succeeded on, such as <c>Started</c>.</param>
    /// <param name="SkippedText">Written after the name of each instance it skipped.</param>
    private sealed record LifecycleAction(
        string Name,
        string Description,
        string ProgressVerb,
        string DoneVerb,
        string SkippedText);
}
