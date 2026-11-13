using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Commands.Instances;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Operations;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Backups;

/// <summary>
/// The <c>instance create-from-backup</c> command: a new instance made from a backup point, such as a copy of an
/// instance, or an instance back after the loss of its machine.
/// </summary>
internal static class CreateFromBackupCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> nameArgument = new("name")
        {
            Description = "Unique name: a letter, then up to 31 letters, digits, '-' or '_'. Also the p4d server ID.",
        };
        Option<string> destinationOption = new("--destination", "-d")
        {
            Description = "The destination that holds the point. Add it again with its passphrase on a new daemon.",
            Required = true,
        };
        Option<string?> pointOption = new("--point")
        {
            Description = "The backup point: its identifier, or the start of it, as 'p4sm backup-destination points' shows.",
        };
        Option<string?> fromOption = new("--from")
        {
            Description = "Restore the latest point of this instance, by the name it had when it was backed up.",
            HelpName = "instance",
        };
        Option<int> portOption = new("--port")
        {
            Description = "TCP port p4d listens on, between 1024 and 65535.",
            Required = true,
        };
        Option<string> superUserOption = new("--super-user")
        {
            Description = "A super user of the restored server. Its password, as it was at the time of the point, is "
                + "asked on the terminal.",
            HelpName = "name",
            Required = true,
        };
        Option<bool> passwordStdinOption = new("--password-stdin")
        {
            Description = "Read the password of the super user from the standard input instead.",
        };
        Option<string?> releaseOption = new("--release")
        {
            Description = "p4d release, for example r25.1. Defaults to the release of the point; it cannot be older.",
        };
        Option<string?> buildOption = new("--build")
        {
            Description = "Exact build of the release from the archive. Defaults to the build of the point.",
        };
        Option<string?> descriptionOption = new("--description") { Description = "Free text." };
        Option<string?> directoryOption = new("--directory")
        {
            Description = "Absolute folder of the server, missing or empty. Defaults to a folder of the instance in the "
                + "data directory.",
            HelpName = "path",
        };
        Option<bool> sslOption = new("--ssl")
        {
            Description = "Listen with SSL, with the certificate of the point when it has one.",
        };
        Option<bool> noLicenseOption = new("--no-license")
        {
            Description = "Leave out the license file of the point, which is bound to the address of its machine.",
        };
        Option<bool> noAutoStartOption = new("--no-auto-start")
        {
            Description = "Do not start p4d when the daemon starts.",
        };
        Option<bool> noWaitOption = new("--no-wait")
        {
            Description = "Return once the instance is created, without waiting for its restore.",
        };

        Command command = new(
            "create-from-backup",
            "Create an instance from a backup point: its database and its archives, with the case handling and the "
            + "Unicode mode of the point. Its server ID is its name; the depots kept outside server.depot.root go under "
            + "it. Exits with an error unless the restore succeeded without a problem.")
        {
            nameArgument,
            destinationOption,
            pointOption,
            fromOption,
            portOption,
            superUserOption,
            passwordStdinOption,
            releaseOption,
            buildOption,
            descriptionOption,
            directoryOption,
            sslOption,
            noLicenseOption,
            noAutoStartOption,
            noWaitOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            string? pointReference = invocation.GetValue(pointOption);
            string? from = invocation.GetValue(fromOption);
            if ((pointReference is null) == (from is null))
            {
                throw new CliException("Name the point with --point, or the instance whose latest point to restore with --from.");
            }

            using DaemonClient client = invocation.CreateClient();
            BackupDestinationDto destination = await client.ResolveBackupDestinationAsync(
                invocation.GetValue(destinationOption),
                cancellationToken);
            IReadOnlyList<BackupPointDto> points = await client.GetBackupPointsAsync(destination.Id, null, cancellationToken);
            BackupPointDto point = pointReference is not null
                ? DaemonClient.ResolveBackupPoint(points, pointReference)
                : points.FirstOrDefault(p => string.Equals(p.SourceName, from, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CliException($"{destination.Name} holds no backup point of {from}.");

            string password = await ReadPasswordAsync(invocation, invocation.GetValue(passwordStdinOption), cancellationToken);
            InstanceDto instance = await client.RestoreInstanceAsync(
                new RestoreInstanceRequest
                {
                    Name = invocation.GetValue(nameArgument),
                    Description = invocation.GetValue(descriptionOption),
                    Port = invocation.GetValue(portOption),
                    Release = invocation.GetValue(releaseOption),
                    Build = invocation.GetValue(buildOption),
                    ServerDirectory = invocation.GetValue(directoryOption),
                    Ssl = invocation.GetValue(sslOption),
                    AutoStart = !invocation.GetValue(noAutoStartOption),
                    IncludeLicense = !invocation.GetValue(noLicenseOption),
                    SuperUser = new SuperUserRequest { Name = invocation.GetValue(superUserOption), Password = password },
                    Source = new BackupPointReference { DestinationId = destination.Id, SnapshotKey = point.Key },
                },
                cancellationToken);

            if (invocation.GetValue(noWaitOption))
            {
                if (invocation.Json)
                {
                    invocation.WriteJson(instance, CliJsonContext.Default.InstanceDto);
                }
                else
                {
                    await invocation.Output.WriteLineAsync(
                        $"Created {instance.Name}. Its restore goes on in the daemon: follow it with "
                        + $"'p4sm instance operation {instance.Name} --follow'.");
                }
                return 0;
            }

            await invocation.Progress.WriteLineAsync($"Restoring {point.SourceName} into {instance.Name}...");
            OperationSnapshot? snapshot;
            try
            {
                snapshot = await OperationWaiter.WaitAsync(invocation, client, instance.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await invocation.Error.WriteLineAsync(OperationWaiter.StoppedWaitingMessage(instance.Name));
                return CliServices.CanceledExitCode;
            }

            int exitCode = await OperationWaiter.ReportEndAsync(invocation, snapshot);
            IReadOnlyList<RestoreRunDto> restores = await client.GetRestoresAsync(instance.Id, cancellationToken);
            RestoreRunDto? run = restores.FirstOrDefault();
            if (run is not null)
            {
                await InstanceRestoreCommand.WriteRunAsync(invocation, run);
                if (run.Status != RestoreRunStatus.Succeeded)
                {
                    exitCode = CliServices.ErrorExitCode;
                }
            }
            return exitCode;
        });
        return command;
    }

    /// <summary>
    /// Reads the password of the super user of the restored server. It is never taken from an argument, and its strength
    /// is not checked: it is the one the server already has.
    /// </summary>
    private static async Task<string> ReadPasswordAsync(
        CliInvocation invocation,
        bool fromStandardInput,
        CancellationToken cancellationToken)
    {
        if (fromStandardInput)
        {
            string? line = await invocation.Environment.Input.ReadLineAsync(cancellationToken);
            return line is { Length: > 0 }
                ? line
                : throw new CliException("No password was read from the standard input.");
        }

        IPrompter prompter = invocation.Environment.Prompter;
        if (!prompter.IsInteractive)
        {
            throw new CliException(
                "The password of the super user is asked on a terminal. Without one, pipe it with --password-stdin.");
        }
        string password = prompter.ReadSecret("Password of the super user of the restored server: ");
        return password.Length > 0 ? password : throw new CliException("The password of the super user cannot be empty.");
    }
}
