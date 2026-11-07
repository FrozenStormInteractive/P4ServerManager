using System.CommandLine;
using System.Globalization;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Output;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

/// <summary>
/// The <c>instance health</c> command and its subcommands: the checks of the database of an instance, and their reports.
/// </summary>
internal static class InstanceHealthCommand
{
    private const string ValidateName = "validate";
    private const string CrossCheckName = "cross-check";

    public static Command Create(CliServices services)
    {
        Command command = new("health", "Check the database of an instance, and read the reports of these checks.");
        command.Subcommands.Add(CreateCheck(services));
        command.Subcommands.Add(CreateList(services));
        command.Subcommands.Add(CreateShow(services));
        command.Subcommands.Add(CreateCancel(services));
        command.Subcommands.Add(CreateDelete(services));
        return command;
    }

    private static Command CreateCheck(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Option<string?> onlyOption = new("--only")
        {
            Description = $"Run one check only: {ValidateName} (p4d -xv) or {CrossCheckName} (p4d -xx).",
        };
        onlyOption.AcceptOnlyFromAmong(ValidateName, CrossCheckName);
        Option<bool> noWaitOption = new("--no-wait")
        {
            Description = "Return once the check has begun, without waiting for its end.",
        };
        Option<bool> yesOption = new("--yes", "-y") { Description = "Check without asking for a confirmation." };
        Command command = new(
            "check",
            "Check the tables of the database and their consistency: stop p4d if it runs, check, then start it again if "
            + "it ran. Exits with an error when problems are found.")
        {
            instanceArgument,
            onlyOption,
            noWaitOption,
            yesOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);

            if (instance.RuntimeStatus == InstanceRuntimeStatus.Running && !invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException(
                        "Checking a running instance stops it, and asks for a confirmation. Without a terminal, pass --yes.");
                }
                string question = $"Check {instance.Name}? It is stopped during the check, which disconnects its users, "
                    + "then started again.";
                if (!prompter.Confirm(question))
                {
                    await invocation.Error.WriteLineAsync("Nothing was checked.");
                    return CliServices.ErrorExitCode;
                }
            }

            IReadOnlyList<HealthCheckKind> checks = invocation.GetValue(onlyOption) switch
            {
                ValidateName => [HealthCheckKind.Validate],
                CrossCheckName => [HealthCheckKind.CrossCheck],
                _ => [HealthCheckKind.Validate, HealthCheckKind.CrossCheck],
            };
            HealthCheckRunDto run = await client.StartHealthCheckAsync(
                instance.Id,
                new StartHealthCheckRequest { Checks = checks },
                cancellationToken);

            if (invocation.GetValue(noWaitOption))
            {
                if (invocation.Json)
                {
                    invocation.WriteJson(run, CliJsonContext.Default.HealthCheckRunDto);
                }
                else
                {
                    await invocation.Output.WriteLineAsync(
                        $"Checking {instance.Name}. The check goes on in the daemon: read it with "
                        + $"'p4sm instance health show {instance.Name}'.");
                }
                return 0;
            }

            await invocation.Progress.WriteLineAsync($"Checking {instance.Name}...");
            InstanceDto after;
            try
            {
                run = await FollowAsync(invocation, client, run, cancellationToken);
                after = await WaitForServerAsync(invocation, client, instance.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await invocation.Error.WriteLineAsync(
                    "Stopped waiting. The check goes on in the daemon: stop it with "
                    + $"'p4sm instance health cancel {instance.Name}'.");
                return CliServices.CanceledExitCode;
            }

            if (invocation.Json)
            {
                invocation.WriteJson(run, CliJsonContext.Default.HealthCheckRunDto);
            }
            else
            {
                await invocation.Output.WriteLineAsync(FormatStatus(run));
                if (run.Error is not null)
                {
                    await invocation.Error.WriteLineAsync($"Error: {run.Error}");
                }
            }

            bool serverDown = instance.RuntimeStatus == InstanceRuntimeStatus.Running
                && after.RuntimeStatus != InstanceRuntimeStatus.Running;
            if (serverDown)
            {
                await invocation.Error.WriteLineAsync($"Error: {after.LastError ?? $"{instance.Name} did not start again."}");
            }
            return run.Status == HealthCheckStatus.Healthy && !serverDown ? 0 : CliServices.ErrorExitCode;
        });
        return command;
    }

    private static Command CreateList(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Command command = new("list", "List the reports of the checks of an instance, the latest first.") { instanceArgument };
        command.Aliases.Add("ls");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);
            IReadOnlyList<HealthCheckRunDto> runs = await GetReportsAsync(client, instance.Id, cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(runs, CliJsonContext.Default.IReadOnlyListHealthCheckRunDto);
                return 0;
            }
            if (runs.Count == 0)
            {
                await invocation.Output.WriteLineAsync(
                    $"No reports. Check the instance with 'p4sm instance health check {instance.Name}'.");
                return 0;
            }

            TableWriter table = new("ID", "STARTED", "STATUS", "CHECKS", "PROBLEMS", "P4D");
            foreach (HealthCheckRunDto run in runs)
            {
                table.AddRow(
                    ShortId(run),
                    FormatDate(run.StartedAt),
                    run.Status.ToString(),
                    FormatChecks(run),
                    run.Findings.Count.ToString(CultureInfo.InvariantCulture),
                    run.P4dVersion);
            }
            table.Write(invocation.Output);
            return 0;
        });
        return command;
    }

    private static Command CreateShow(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Argument<string?> runArgument = CreateRunArgument("The report. Defaults to the latest one.");
        runArgument.Arity = ArgumentArity.ZeroOrOne;
        Command command = new("show", "Show a report of a check of an instance, with the problems it found.")
        {
            instanceArgument,
            runArgument,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);
            IReadOnlyList<HealthCheckRunDto> runs = await GetReportsAsync(client, instance.Id, cancellationToken);
            HealthCheckRunDto run = invocation.GetValue(runArgument) is { } reference
                ? ResolveRun(runs, reference)
                : runs.FirstOrDefault() ?? throw new CliException($"{instance.Name} has no reports.");

            if (invocation.Json)
            {
                invocation.WriteJson(run, CliJsonContext.Default.HealthCheckRunDto);
                return 0;
            }

            List<(string Label, string? Value)> fields =
            [
                ("Id", run.Id.ToString()),
                ("Status", run.Status.ToString()),
                ("Checks", FormatChecks(run)),
                ("p4d", run.P4dVersion),
                ("Started", FormatDate(run.StartedAt)),
                ("Finished", run.FinishedAt is { } finishedAt ? FormatDate(finishedAt) : null),
                ("Progress", run.Progress is { } progress
                    ? $"{progress.Percent.ToString(CultureInfo.InvariantCulture)}% {progress.Item}".TrimEnd()
                    : null),
                ("Error", run.Error),
            ];
            int width = fields.Max(f => f.Label.Length) + 2;
            foreach ((string label, string? value) in fields.Where(f => !string.IsNullOrEmpty(f.Value)))
            {
                await invocation.Output.WriteLineAsync($"{(label + ":").PadRight(width)}{value}");
            }
            foreach (HealthCheckFindingDto finding in run.Findings)
            {
                await invocation.Output.WriteLineAsync(FormatFinding(finding));
            }
            return 0;
        });
        return command;
    }

    private static Command CreateCancel(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Command command = new("cancel", "Stop the check running on an instance. What it found until then is kept.")
        {
            instanceArgument,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);
            IReadOnlyList<HealthCheckRunDto> runs = await client.GetHealthChecksAsync(instance.Id, cancellationToken);
            HealthCheckRunDto run = runs.FirstOrDefault(r => r.Status == HealthCheckStatus.Running)
                ?? throw new CliException($"No check runs on {instance.Name}.");

            await client.CancelHealthCheckAsync(instance.Id, run.Id, cancellationToken);
            await invocation.Progress.WriteLineAsync($"Stopping the check of {instance.Name}.");
            return 0;
        });
        return command;
    }

    private static Command CreateDelete(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Argument<string?> runArgument = CreateRunArgument("The report.");
        Option<bool> yesOption = new("--yes", "-y") { Description = "Delete without asking for a confirmation." };
        Command command = new("delete", "Delete a report of a check of an instance.")
        {
            instanceArgument,
            runArgument,
            yesOption,
        };
        command.Aliases.Add("rm");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);
            IReadOnlyList<HealthCheckRunDto> runs = await GetReportsAsync(client, instance.Id, cancellationToken);
            HealthCheckRunDto run = ResolveRun(runs, invocation.GetValue(runArgument)!);

            if (!invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException("Deleting a report asks for a confirmation. Without a terminal, pass --yes.");
                }
                if (!prompter.Confirm($"Delete the report {ShortId(run)} of {FormatDate(run.StartedAt)}?"))
                {
                    await invocation.Error.WriteLineAsync("Nothing was deleted.");
                    return CliServices.ErrorExitCode;
                }
            }

            await client.DeleteHealthCheckAsync(instance.Id, run.Id, cancellationToken);
            if (!invocation.Json)
            {
                await invocation.Output.WriteLineAsync($"Deleted the report {ShortId(run)}.");
            }
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Reads a run until it ends. Writes each table it reaches and each problem it finds.
    /// </summary>
    private static async Task<HealthCheckRunDto> FollowAsync(
        CliInvocation invocation,
        DaemonClient client,
        HealthCheckRunDto run,
        CancellationToken cancellationToken)
    {
        string? lastItem = null;
        int findingsWritten = 0;
        while (true)
        {
            foreach (HealthCheckFindingDto finding in run.Findings.Skip(findingsWritten))
            {
                await invocation.Progress.WriteLineAsync(FormatFinding(finding));
            }
            findingsWritten = run.Findings.Count;

            if (run.Status != HealthCheckStatus.Running)
            {
                return run;
            }
            if (run.Progress is { Item: { } item } progress && item != lastItem)
            {
                lastItem = item;
                await invocation.Progress.WriteLineAsync(
                    $"[{progress.Percent.ToString(CultureInfo.InvariantCulture),3}%] {item}");
            }

            await Task.Delay(invocation.Environment.PollInterval, cancellationToken);
            run = await client.GetHealthCheckAsync(run.InstanceId, run.Id, cancellationToken);
        }
    }

    /// <summary>
    /// Reads the instance until the check has released it: p4d starts again once the run has ended.
    /// </summary>
    private static async Task<InstanceDto> WaitForServerAsync(
        CliInvocation invocation,
        DaemonClient client,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            InstanceDto instance = await client.GetInstanceAsync(instanceId, cancellationToken);
            if (instance.RuntimeStatus != InstanceRuntimeStatus.CheckingHealth)
            {
                return instance;
            }
            await Task.Delay(invocation.Environment.PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// The reports, without the deleted ones the daemon keeps while they tell the health of the instance.
    /// </summary>
    private static async Task<IReadOnlyList<HealthCheckRunDto>> GetReportsAsync(
        DaemonClient client,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HealthCheckRunDto> runs = await client.GetHealthChecksAsync(instanceId, cancellationToken);
        return runs.Where(r => !r.ReportHidden).ToList();
    }

    private static Argument<string?> CreateRunArgument(string description)
    {
        return new Argument<string?>("report")
        {
            Description = $"{description} Its identifier, or the start of it, as 'list' shows.",
        };
    }

    private static HealthCheckRunDto ResolveRun(IReadOnlyList<HealthCheckRunDto> runs, string reference)
    {
        List<HealthCheckRunDto> matches = runs
            .Where(r => r.Id.ToString().StartsWith(reference, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No report matches {reference}."),
            _ => throw new CliException($"Several reports match {reference}. Give more of its identifier."),
        };
    }

    private static string ShortId(HealthCheckRunDto run)
    {
        return run.Id.ToString()[..8];
    }

    private static string FormatDate(DateTimeOffset date)
    {
        return date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    private static string FormatChecks(HealthCheckRunDto run)
    {
        return string.Join(", ", run.Checks.Select(c => c == HealthCheckKind.Validate ? ValidateName : CrossCheckName));
    }

    private static string FormatFinding(HealthCheckFindingDto finding)
    {
        return finding.Table is null || finding.Message.Contains(finding.Table, StringComparison.Ordinal)
            ? $"[problem] {finding.Message}"
            : $"[problem] {finding.Table}: {finding.Message}";
    }

    private static string FormatStatus(HealthCheckRunDto run)
    {
        return run.Status switch
        {
            HealthCheckStatus.Healthy => "Healthy: no problems found.",
            HealthCheckStatus.ProblemsFound =>
                $"Problems found: {run.Findings.Count.ToString(CultureInfo.InvariantCulture)}. Ask Perforce Support before any repair.",
            HealthCheckStatus.Canceled => "The check was canceled.",
            HealthCheckStatus.Interrupted => "The check was interrupted by the end of the daemon.",
            _ => "The check could not run to its end.",
        };
    }
}
