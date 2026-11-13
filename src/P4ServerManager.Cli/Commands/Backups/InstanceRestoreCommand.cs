using System.CommandLine;
using System.Globalization;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Commands.Instances;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Operations;
using P4ServerManager.Cli.Output;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Backups;

/// <summary>
/// The <c>instance restore</c> command and its subcommands: restores of backup points over the data of an instance.
/// </summary>
internal static class InstanceRestoreCommand
{
    public static Command Create(CliServices services)
    {
        Command command = new("restore", "Replace the data of an instance with a backup point, and list its restores.");
        command.Subcommands.Add(CreateStart(services));
        command.Subcommands.Add(CreateList(services));
        command.Subcommands.Add(CreateShow(services));
        command.Subcommands.Add(CreateCancel(services));
        return command;
    }

    private static Command CreateStart(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Argument<string?> pointArgument = new("point")
        {
            Description = "The backup point: its identifier, or the start of it, as 'p4sm backup-destination points' "
                + "or 'p4sm instance backup list' shows. Defaults to the latest point of the instance.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Option<string?> destinationOption = new("--destination", "-d")
        {
            Description = "The destination to read the point from. Defaults to the destinations of the instance.",
        };
        Option<bool> yesOption = new("--yes", "-y")
        {
            Description = "Replace the data without asking for a confirmation.",
        };
        Option<bool> noWaitOption = new("--no-wait")
        {
            Description = "Return once the restore has begun, without waiting for its end.",
        };
        Command command = new(
            "start",
            "Replace the data of an instance with a backup point: p4d stops, the current data is kept aside, the "
            + "database is rebuilt from the point and its archives put back, then p4d starts and 'p4 verify' checks "
            + "the archives. The previous data is deleted once the restore succeeds, and put back if it fails. Exits "
            + "with an error unless the restore succeeded without a problem.")
        {
            instanceArgument,
            pointArgument,
            destinationOption,
            yesOption,
            noWaitOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(invocation.GetValue(instanceArgument), cancellationToken);
            (BackupDestinationDto destination, BackupPointDto point) = await FindPointAsync(
                client,
                instance,
                invocation.GetValue(pointArgument),
                invocation.GetValue(destinationOption),
                cancellationToken);

            if (!invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException(
                        "Replacing the data of an instance asks for a confirmation. Without a terminal, pass --yes.");
                }
                string question = $"Replace the data of {instance.Name} with the {point.Kind.ToString().ToLowerInvariant()} "
                    + $"backup of {point.SourceName} made {FormatDate(point.CreatedAt)}, from {destination.Name}?";
                if (!prompter.Confirm(question))
                {
                    await invocation.Error.WriteLineAsync("Nothing was restored.");
                    return CliServices.ErrorExitCode;
                }
            }

            RestoreRunDto run = await client.StartRestoreAsync(
                instance.Id,
                new RestoreBackupRequest { DestinationId = destination.Id, SnapshotKey = point.Key },
                cancellationToken);
            if (invocation.GetValue(noWaitOption))
            {
                if (invocation.Json)
                {
                    invocation.WriteJson(run, CliJsonContext.Default.RestoreRunDto);
                }
                else
                {
                    await invocation.Output.WriteLineAsync(
                        $"Restoring {instance.Name}. The restore goes on in the daemon: read it with "
                        + $"'p4sm instance restore show {instance.Name}'.");
                }
                return 0;
            }

            try
            {
                await OperationWaiter.WaitAsync(invocation, client, instance.Id, cancellationToken);
                run = await WaitForEndAsync(invocation, client, instance.Id, run.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await invocation.Error.WriteLineAsync(
                    "Stopped waiting. The restore goes on in the daemon: stop it with "
                    + $"'p4sm instance restore cancel {instance.Name}'.");
                return CliServices.CanceledExitCode;
            }

            await WriteRunAsync(invocation, run);
            return run.Status == RestoreRunStatus.Succeeded ? 0 : CliServices.ErrorExitCode;
        });
        return command;
    }

    private static Command CreateList(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Command command = new("list", "List the restores of an instance, the latest first.") { instanceArgument };
        command.Aliases.Add("ls");
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(invocation.GetValue(instanceArgument), cancellationToken);
            IReadOnlyList<RestoreRunDto> runs = await client.GetRestoresAsync(instance.Id, cancellationToken);
            if (invocation.Json)
            {
                invocation.WriteJson(runs, CliJsonContext.Default.IReadOnlyListRestoreRunDto);
                return 0;
            }
            if (runs.Count == 0)
            {
                await invocation.Output.WriteLineAsync($"{instance.Name} was never restored.");
                return 0;
            }

            TableWriter table = new("ID", "STARTED", "POINT", "FROM", "STATUS");
            foreach (RestoreRunDto run in runs)
            {
                table.AddRow(
                    ShortId(run.Id),
                    FormatDate(run.StartedAt),
                    ShortId(run.PointId),
                    run.DestinationName,
                    run.Status.ToString());
            }
            table.Write(invocation.Output);
            return 0;
        });
        return command;
    }

    private static Command CreateShow(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Argument<string?> runArgument = new("restore")
        {
            Description = "The restore: its identifier, or the start of it, as 'list' shows. Defaults to the latest one.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Command command = new("show", "Show a restore of an instance.") { instanceArgument, runArgument };
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(invocation.GetValue(instanceArgument), cancellationToken);
            IReadOnlyList<RestoreRunDto> runs = await client.GetRestoresAsync(instance.Id, cancellationToken);
            RestoreRunDto run = invocation.GetValue(runArgument) is { } reference
                ? ResolveRun(runs, reference)
                : runs.FirstOrDefault() ?? throw new CliException($"{instance.Name} was never restored.");
            await WriteRunAsync(invocation, run);
            return 0;
        });
        return command;
    }

    private static Command CreateCancel(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Command command = new(
            "cancel",
            "Stop the restore running on an instance. Before p4d starts on the restored data, the previous data comes "
            + "back; during the verification of the archives, only the verification stops.")
        {
            instanceArgument,
        };
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(invocation.GetValue(instanceArgument), cancellationToken);
            IReadOnlyList<RestoreRunDto> runs = await client.GetRestoresAsync(instance.Id, cancellationToken);
            RestoreRunDto run = runs.FirstOrDefault(r => r.Status == RestoreRunStatus.Running)
                ?? throw new CliException($"No restore runs on {instance.Name}.");
            await client.CancelRestoreAsync(instance.Id, run.Id, cancellationToken);
            await invocation.Progress.WriteLineAsync($"Stopping the restore of {instance.Name}.");
            return 0;
        });
        return command;
    }

    /// <summary>
    /// The point a restore over <paramref name="instance"/> reads: the one asked for, or the latest of the instance, at
    /// the destination asked for or at the destinations of the instance.
    /// </summary>
    private static async Task<(BackupDestinationDto Destination, BackupPointDto Point)> FindPointAsync(
        DaemonClient client,
        InstanceDto instance,
        string? pointReference,
        string? destinationReference,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BackupDestinationDto> all = await client.GetBackupDestinationsAsync(cancellationToken);
        List<BackupDestinationDto> destinations;
        if (destinationReference is not null)
        {
            destinations = [DaemonClient.ResolveBackupDestination(all, destinationReference)];
        }
        else
        {
            InstanceBackupDestinations selection = await client.GetInstanceBackupDestinationsAsync(instance.Id, cancellationToken);
            destinations = all.Where(d => selection.DestinationIds.Contains(d.Id)).ToList();
            if (destinations.Count == 0)
            {
                throw new CliException($"{instance.Name} backs up nowhere: name the destination with --destination.");
            }
        }

        List<(BackupDestinationDto Destination, BackupPointDto Point)> found = [];
        foreach (BackupDestinationDto destination in destinations)
        {
            // A point of another instance is asked for by its identifier only.
            Guid? sourceId = pointReference is null ? instance.Id : null;
            foreach (BackupPointDto point in await client.GetBackupPointsAsync(destination.Id, sourceId, cancellationToken))
            {
                found.Add((destination, point));
            }
        }

        if (pointReference is null)
        {
            return found.Count > 0
                ? found.MaxBy(f => f.Point.CreatedAt)
                : throw new CliException($"No backup point of {instance.Name} is stored at {string.Join(", ", destinations.Select(d => d.Name))}.");
        }

        BackupPointDto chosen = DaemonClient.ResolveBackupPoint(found.Select(f => f.Point).ToList(), pointReference);
        return found.First(f => f.Point.Id == chosen.Id);
    }

    private static async Task<RestoreRunDto> WaitForEndAsync(
        CliInvocation invocation,
        DaemonClient client,
        Guid instanceId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            RestoreRunDto run = await client.GetRestoreAsync(instanceId, runId, cancellationToken);
            if (run.Status != RestoreRunStatus.Running)
            {
                return run;
            }
            await Task.Delay(invocation.Environment.PollInterval, cancellationToken);
        }
    }

    public static async Task WriteRunAsync(CliInvocation invocation, RestoreRunDto run)
    {
        if (invocation.Json)
        {
            invocation.WriteJson(run, CliJsonContext.Default.RestoreRunDto);
            return;
        }

        List<(string Label, string? Value)> fields =
        [
            ("Id", run.Id.ToString()),
            ("Instance", run.InstanceName),
            ("Target", run.Target == RestoreTarget.NewInstance ? "New instance" : "Existing instance"),
            ("Status", run.Status.ToString()),
            ("Point", $"{run.PointId} of {run.SourceName ?? "?"}{(run.PointCreatedAt is { } made ? $", made {FormatDate(made)}" : string.Empty)}"),
            ("From", run.DestinationName),
            ("Started", FormatDate(run.StartedAt)),
            ("Finished", run.FinishedAt is { } finishedAt ? FormatDate(finishedAt) : null),
            ("Files", $"{run.FileCount.ToString(CultureInfo.InvariantCulture)} ({BackupPointFormatting.FormatBytes(run.TotalBytes)})"),
            ("Downloaded", BackupPointFormatting.FormatBytes(run.DownloadedBytes)),
            ("Kept", run.ReusedFiles > 0 ? $"{run.ReusedFiles.ToString(CultureInfo.InvariantCulture)} unchanged files" : null),
            ("Verify", run.Status is RestoreRunStatus.Succeeded or RestoreRunStatus.SucceededWithProblems
                ? $"{run.VerifyProblemCount.ToString(CultureInfo.InvariantCulture)} problems"
                : null),
            ("Error", run.Error),
        ];
        int width = fields.Max(f => f.Label.Length) + 2;
        foreach ((string label, string? value) in fields.Where(f => !string.IsNullOrEmpty(f.Value)))
        {
            await invocation.Output.WriteLineAsync($"{(label + ":").PadRight(width)}{value}");
        }
        foreach (string warning in run.Warnings)
        {
            await invocation.Output.WriteLineAsync($"Warning: {warning}");
        }
        foreach (string problem in run.VerifyProblems)
        {
            await invocation.Output.WriteLineAsync($"[problem] {problem}");
        }
    }

    private static RestoreRunDto ResolveRun(IReadOnlyList<RestoreRunDto> runs, string reference)
    {
        List<RestoreRunDto> matches = runs
            .Where(r => r.Id.ToString().StartsWith(reference, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No restore matches {reference}."),
            _ => throw new CliException($"Several restores match {reference}. Give more of its identifier."),
        };
    }

    private static string ShortId(Guid id)
    {
        return id.ToString()[..8];
    }

    private static string FormatDate(DateTimeOffset date)
    {
        return BackupPointFormatting.FormatDate(date);
    }
}

/// <summary>
/// How the commands print backup points.
/// </summary>
internal static class BackupPointFormatting
{
    public static void WriteTable(TextWriter output, IReadOnlyList<BackupPointDto> points)
    {
        TableWriter table = new("ID", "CREATED", "INSTANCE", "KIND", "CHECKPOINT", "JOURNAL", "P4D");
        foreach (BackupPointDto point in points)
        {
            table.AddRow(
                point.Id.ToString()[..8],
                FormatDate(point.CreatedAt),
                point.SourceName,
                point.Kind.ToString(),
                point.CheckpointNumber?.ToString(CultureInfo.InvariantCulture) ?? "-",
                point.JournalNumber?.ToString(CultureInfo.InvariantCulture) ?? "-",
                point.P4dVersion ?? "-");
        }
        table.Write(output);
    }

    public static string FormatDate(DateTimeOffset date)
    {
        return date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
