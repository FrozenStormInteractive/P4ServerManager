using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Operations;
using P4ServerManager.Cli.Output;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class UpgradeInstanceCommand
{
    /// <summary>
    /// Outcome of an upgrade of an instance that already ran the latest build.
    /// </summary>
    private const string UpToDateOutcome = "up-to-date";

    public static Command Create(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Option<string> releaseOption = new("--release")
        {
            Description = "p4d release, for example r25.2: the release of the instance, for its latest patch, or a "
                + "newer one. The database cannot go back to an older release.",
            Required = true,
        };
        Option<bool> yesOption = new("--yes", "-y") { Description = "Upgrade without asking for a confirmation." };
        Option<bool> noWaitOption = new("--no-wait")
        {
            Description = "Return once the upgrade has begun, without waiting for its end.",
        };
        Command command = new(
            "upgrade",
            "Upgrade the p4d of an instance: stop it, upgrade the database, then start it again if it ran.")
        {
            instanceArgument,
            releaseOption,
            yesOption,
            noWaitOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);
            string release = invocation.GetValue(releaseOption)!;

            if (!invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException(
                        "Upgrading an instance asks for a confirmation. Without a terminal, pass --yes.");
                }
                string question = $"Upgrade {instance.Name} from p4d {InstanceFormatting.FormatVersion(instance)} to "
                    + $"{release}? Its users are disconnected until it starts again, and its database cannot go back.";
                if (!prompter.Confirm(question))
                {
                    await invocation.Error.WriteLineAsync("Nothing was upgraded.");
                    return CliServices.ErrorExitCode;
                }
            }

            InstanceDto upgrading = await client.UpgradeInstanceAsync(
                instance.Id,
                new UpgradeInstanceRequest { Release = release },
                cancellationToken);

            if (invocation.GetValue(noWaitOption))
            {
                if (invocation.Json)
                {
                    invocation.WriteJson(upgrading, CliJsonContext.Default.InstanceDto);
                }
                else
                {
                    await invocation.Output.WriteLineAsync(
                        $"Upgrading {upgrading.Name}. The upgrade goes on in the daemon: follow it with "
                        + $"'p4sm instance operation {upgrading.Name} --follow'.");
                }
                return 0;
            }

            await invocation.Progress.WriteLineAsync($"Upgrading {upgrading.Name} to {release}...");
            OperationSnapshot? snapshot;
            try
            {
                snapshot = await OperationWaiter.WaitAsync(invocation, client, upgrading.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await invocation.Error.WriteLineAsync(OperationWaiter.StoppedWaitingMessage(upgrading.Name));
                return CliServices.CanceledExitCode;
            }

            int exitCode = await OperationWaiter.ReportEndAsync(invocation, snapshot);
            InstanceDto upgraded = await client.GetInstanceAsync(upgrading.Id, cancellationToken);
            if (invocation.Json)
            {
                invocation.WriteJson(upgraded, CliJsonContext.Default.InstanceDto);
            }
            else if (exitCode == 0)
            {
                string version = InstanceFormatting.FormatVersion(upgraded);
                await invocation.Output.WriteLineAsync(snapshot?.Outcome == UpToDateOutcome
                    ? $"{upgraded.Name} already runs the latest build, p4d {version}. Nothing was changed."
                    : $"{upgraded.Name} now runs p4d {version}.");
            }
            else if (upgraded.LastError is { } lastError && lastError != snapshot?.Error)
            {
                // Tells, for example, that the database may be partly upgraded.
                await invocation.Error.WriteLineAsync(lastError);
            }
            return exitCode;
        });
        return command;
    }
}
