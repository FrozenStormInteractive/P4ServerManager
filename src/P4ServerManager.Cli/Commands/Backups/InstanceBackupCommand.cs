using System.CommandLine;
using System.Globalization;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Commands.Instances;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Operations;
using P4ServerManager.Cli.Output;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Backups;

/// <summary>
/// The <c>instance backup</c> command and its subcommands: the backups of an instance, their destinations and their
/// verification.
/// </summary>
internal static class InstanceBackupCommand
{
    public static Command Create(CliServices services)
    {
        Command command = new("backup", "Back up an instance, list its backups, and choose where they go.");
        command.Subcommands.Add(CreateStart(services));
        command.Subcommands.Add(CreateList(services));
        command.Subcommands.Add(CreateShow(services));
        command.Subcommands.Add(CreateCancel(services));
        command.Subcommands.Add(CreateVerify(services));
        command.Subcommands.Add(CreateDestinations(services));
        return command;
    }

    private static Command CreateStart(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Option<bool> incrementalOption = new("--incremental", "-i")
        {
            Description = "Rotate the journal on top of the latest backup at each destination, instead of a checkpoint.",
        };
        Option<string[]> destinationOption = new("--destination", "-d")
        {
            Description = "Back up to this destination of the instance only. Repeat for several. All by default.",
            AllowMultipleArgumentsPerToken = true,
        };
        Option<bool> noWaitOption = new("--no-wait")
        {
            Description = "Return once the backup has begun, without waiting for its end.",
        };
        Command command = new(
            "start",
            "Back up an instance while it keeps running: a checkpoint, or a rotation of the journal with --incremental, "
            + "then the files it lacks uploaded to each destination. Exits with an error unless every destination has "
            + "the backup.")
        {
            instanceArgument,
            incrementalOption,
            destinationOption,
            noWaitOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(invocation.GetValue(instanceArgument), cancellationToken);
            string[] destinationNames = invocation.GetValue(destinationOption) ?? [];
            List<Guid>? destinationIds = null;
            if (destinationNames.Length > 0)
            {
                IReadOnlyList<BackupDestinationDto> destinations = await client.GetBackupDestinationsAsync(cancellationToken);
                destinationIds = destinationNames
                    .Select(name => DaemonClient.ResolveBackupDestination(destinations, name).Id)
                    .Distinct()
                    .ToList();
            }

            BackupRunDto run = await client.StartBackupAsync(
                instance.Id,
                new StartBackupRequest
                {
                    Kind = invocation.GetValue(incrementalOption) ? BackupKind.Incremental : BackupKind.Full,
                    DestinationIds = destinationIds,
                },
                cancellationToken);

            if (invocation.GetValue(noWaitOption))
            {
                if (invocation.Json)
                {
                    invocation.WriteJson(run, CliJsonContext.Default.BackupRunDto);
                }
                else
                {
                    await invocation.Output.WriteLineAsync(
                        $"Backing up {instance.Name}. The backup goes on in the daemon: read it with "
                        + $"'p4sm instance backup show {instance.Name}'.");
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
                    "Stopped waiting. The backup goes on in the daemon: stop it with "
                    + $"'p4sm instance backup cancel {instance.Name}'.");
                return CliServices.CanceledExitCode;
            }

            await WriteRunAsync(invocation, run);
            return run.Status == BackupRunStatus.Succeeded ? 0 : CliServices.ErrorExitCode;
        });
        return command;
    }

    private static Command CreateList(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Command command = new("list", "List the backups of an instance, the latest first.") { instanceArgument };
        command.Aliases.Add("ls");
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(invocation.GetValue(instanceArgument), cancellationToken);
            IReadOnlyList<BackupRunDto> runs = await client.GetBackupsAsync(instance.Id, cancellationToken);
            if (invocation.Json)
            {
                invocation.WriteJson(runs, CliJsonContext.Default.IReadOnlyListBackupRunDto);
                return 0;
            }
            if (runs.Count == 0)
            {
                await invocation.Output.WriteLineAsync(
                    $"No backups. Back the instance up with 'p4sm instance backup start {instance.Name}'.");
                return 0;
            }

            TableWriter table = new("ID", "STARTED", "KIND", "STATUS", "FILES", "DESTINATIONS");
            foreach (BackupRunDto run in runs)
            {
                table.AddRow(
                    ShortId(run.Id),
                    FormatDate(run.StartedAt),
                    run.Kind.ToString(),
                    run.Status.ToString(),
                    run.FileCount.ToString(CultureInfo.InvariantCulture),
                    string.Join(", ", run.Destinations.Select(d => $"{d.DestinationName} ({d.Status})")));
            }
            table.Write(invocation.Output);
            return 0;
        });
        return command;
    }

    private static Command CreateShow(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Argument<string?> runArgument = CreateRunArgument("The backup. Defaults to the latest one.");
        runArgument.Arity = ArgumentArity.ZeroOrOne;
        Command command = new("show", "Show a backup of an instance, with what it did at each destination.")
        {
            instanceArgument,
            runArgument,
        };
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(invocation.GetValue(instanceArgument), cancellationToken);
            IReadOnlyList<BackupRunDto> runs = await client.GetBackupsAsync(instance.Id, cancellationToken);
            BackupRunDto run = invocation.GetValue(runArgument) is { } reference
                ? ResolveRun(runs, reference)
                : runs.FirstOrDefault() ?? throw new CliException($"{instance.Name} has no backups.");
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
            "Stop the backup running on an instance. The destinations it had not finished keep no backup point.")
        {
            instanceArgument,
        };
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(invocation.GetValue(instanceArgument), cancellationToken);
            IReadOnlyList<BackupRunDto> runs = await client.GetBackupsAsync(instance.Id, cancellationToken);
            BackupRunDto run = runs.FirstOrDefault(r => r.Status == BackupRunStatus.Running)
                ?? throw new CliException($"No backup runs on {instance.Name}.");
            await client.CancelBackupAsync(instance.Id, run.Id, cancellationToken);
            await invocation.Progress.WriteLineAsync($"Stopping the backup of {instance.Name}.");
            return 0;
        });
        return command;
    }

    private static Command CreateVerify(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Argument<string?> runArgument = CreateRunArgument("The backup. Defaults to the latest one.");
        runArgument.Arity = ArgumentArity.ZeroOrOne;
        Option<string?> destinationOption = new("--destination", "-d")
        {
            Description = "The destination to check. Defaults to the only one of the backup.",
        };
        Option<bool> deepOption = new("--deep")
        {
            Description = "Read every object back and check its content, which downloads the whole backup.",
        };
        Command command = new(
            "verify",
            "Check a backup at one of its destinations: that every object is there, or with --deep that every object "
            + "decrypts to the content it should hold. Exits with an error when a problem is found.")
        {
            instanceArgument,
            runArgument,
            destinationOption,
            deepOption,
        };
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(invocation.GetValue(instanceArgument), cancellationToken);
            IReadOnlyList<BackupRunDto> runs = await client.GetBackupsAsync(instance.Id, cancellationToken);
            BackupRunDto run = invocation.GetValue(runArgument) is { } reference
                ? ResolveRun(runs, reference)
                : runs.FirstOrDefault(r => r.Destinations.Any(d => d.SnapshotKey is not null))
                    ?? throw new CliException($"{instance.Name} has no backup to verify.");
            List<BackupRunDestinationDto> candidates = run.Destinations.Where(d => d.SnapshotKey is not null).ToList();
            BackupRunDestinationDto destination = invocation.GetValue(destinationOption) is { } name
                ? candidates.FirstOrDefault(d => string.Equals(d.DestinationName, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CliException($"Backup {ShortId(run.Id)} has no point at {name}.")
                : candidates.Count == 1
                    ? candidates[0]
                    : throw new CliException(
                        $"Backup {ShortId(run.Id)} went to several destinations: choose one with --destination.");

            DateTimeOffset? previous = destination.LastVerification?.VerifiedAt;
            await client.VerifyBackupAsync(
                instance.Id,
                run.Id,
                new VerifyBackupRequest { DestinationId = destination.DestinationId, Deep = invocation.GetValue(deepOption) },
                cancellationToken);
            OperationSnapshot? snapshot = await OperationWaiter.WaitAsync(invocation, client, instance.Id, cancellationToken);
            run = await client.GetBackupAsync(instance.Id, run.Id, cancellationToken);
            BackupVerificationDto? verification = run.Destinations
                .Single(d => d.DestinationId == destination.DestinationId)
                .LastVerification;
            if (verification is null || verification.VerifiedAt == previous)
            {
                return await OperationWaiter.ReportEndAsync(invocation, snapshot);
            }

            if (invocation.Json)
            {
                invocation.WriteJson(run, CliJsonContext.Default.BackupRunDto);
            }
            else
            {
                await invocation.Output.WriteLineAsync(FormatVerification(verification));
                foreach (string problem in verification.Problems)
                {
                    await invocation.Output.WriteLineAsync($"[problem] {problem}");
                }
            }
            return verification.Succeeded ? 0 : CliServices.ErrorExitCode;
        });
        return command;
    }

    private static Command CreateDestinations(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Option<string[]> setOption = new("--set")
        {
            Description = "Back up to these destinations from now on, by name. Pass --set with no name to back up nowhere.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
        };
        Command command = new("destinations", "Show or set the destinations an instance backs up to.")
        {
            instanceArgument,
            setOption,
        };
        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(invocation.GetValue(instanceArgument), cancellationToken);
            IReadOnlyList<BackupDestinationDto> destinations = await client.GetBackupDestinationsAsync(cancellationToken);
            InstanceBackupDestinations selection;
            if (invocation.ParseResult.GetResult(setOption) is not null)
            {
                selection = await client.SetInstanceBackupDestinationsAsync(
                    instance.Id,
                    new InstanceBackupDestinations
                    {
                        DestinationIds = (invocation.GetValue(setOption) ?? [])
                            .Select(name => DaemonClient.ResolveBackupDestination(destinations, name).Id)
                            .Distinct()
                            .ToList(),
                    },
                    cancellationToken);
            }
            else
            {
                selection = await client.GetInstanceBackupDestinationsAsync(instance.Id, cancellationToken);
            }

            if (invocation.Json)
            {
                invocation.WriteJson(selection, CliJsonContext.Default.InstanceBackupDestinations);
                return 0;
            }
            if (selection.DestinationIds.Count == 0)
            {
                await invocation.Output.WriteLineAsync($"{instance.Name} backs up nowhere.");
                return 0;
            }
            TableWriter table = new("NAME", "KIND", "LOCATION");
            foreach (BackupDestinationDto destination in destinations.Where(d => selection.DestinationIds.Contains(d.Id)))
            {
                table.AddRow(destination.Name, BackupDestinationCommand.FormatKind(destination.Kind), destination.Location);
            }
            table.Write(invocation.Output);
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Reads the backup until the daemon has saved its end.
    /// </summary>
    private static async Task<BackupRunDto> WaitForEndAsync(
        CliInvocation invocation,
        DaemonClient client,
        Guid instanceId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            BackupRunDto run = await client.GetBackupAsync(instanceId, runId, cancellationToken);
            if (run.Status != BackupRunStatus.Running)
            {
                return run;
            }
            await Task.Delay(invocation.Environment.PollInterval, cancellationToken);
        }
    }

    private static async Task WriteRunAsync(CliInvocation invocation, BackupRunDto run)
    {
        if (invocation.Json)
        {
            invocation.WriteJson(run, CliJsonContext.Default.BackupRunDto);
            return;
        }

        List<(string Label, string? Value)> fields =
        [
            ("Id", run.Id.ToString()),
            ("Instance", run.InstanceName),
            ("Kind", run.Kind.ToString()),
            ("Status", run.Status.ToString()),
            ("Started", FormatDate(run.StartedAt)),
            ("Finished", run.FinishedAt is { } finishedAt ? FormatDate(finishedAt) : null),
            ("Checkpoint", run.CheckpointNumber?.ToString(CultureInfo.InvariantCulture)),
            ("Journal", run.JournalNumber?.ToString(CultureInfo.InvariantCulture)),
            ("Files", $"{run.FileCount.ToString(CultureInfo.InvariantCulture)} ({FormatBytes(run.TotalBytes)})"),
            ("Error", run.Error),
        ];
        int width = fields.Max(f => f.Label.Length) + 2;
        foreach ((string label, string? value) in fields.Where(f => !string.IsNullOrEmpty(f.Value)))
        {
            await invocation.Output.WriteLineAsync($"{(label + ":").PadRight(width)}{value}");
        }

        foreach (BackupRunDestinationDto destination in run.Destinations)
        {
            string line = destination.Status == BackupDestinationStatus.Succeeded
                ? $"{destination.DestinationName}: {destination.Status}, {destination.UploadedObjects.ToString(CultureInfo.InvariantCulture)} objects uploaded ({FormatBytes(destination.UploadedBytes)})"
                : $"{destination.DestinationName}: {destination.Status}{(destination.Error is null ? string.Empty : $": {destination.Error}")}";
            await invocation.Output.WriteLineAsync(line);
            if (destination.LastVerification is { } verification)
            {
                await invocation.Output.WriteLineAsync($"  {FormatVerification(verification)}");
            }
        }
    }

    private static string FormatVerification(BackupVerificationDto verification)
    {
        string kind = verification.Deep ? "Deep verification" : "Verification";
        string when = FormatDate(verification.VerifiedAt);
        if (verification.Error is not null)
        {
            return $"{kind} of {when} could not run: {verification.Error}";
        }
        return verification.Succeeded
            ? $"{kind} of {when}: {verification.CheckedObjects.ToString(CultureInfo.InvariantCulture)} objects, nothing wrong."
            : $"{kind} of {when}: {verification.ProblemCount.ToString(CultureInfo.InvariantCulture)} problems in {verification.CheckedObjects.ToString(CultureInfo.InvariantCulture)} objects.";
    }

    private static Argument<string?> CreateRunArgument(string description)
    {
        return new Argument<string?>("backup")
        {
            Description = $"{description} Its identifier, or the start of it, as 'list' shows.",
        };
    }

    private static BackupRunDto ResolveRun(IReadOnlyList<BackupRunDto> runs, string reference)
    {
        List<BackupRunDto> matches = runs
            .Where(r => r.Id.ToString().StartsWith(reference, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No backup matches {reference}."),
            _ => throw new CliException($"Several backups match {reference}. Give more of its identifier."),
        };
    }

    private static string ShortId(Guid id)
    {
        return id.ToString()[..8];
    }

    private static string FormatDate(DateTimeOffset date)
    {
        return date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(long bytes)
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
