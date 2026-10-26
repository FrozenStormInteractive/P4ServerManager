using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Operations;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

internal static class CreateInstanceCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> nameArgument = new("name")
        {
            Description = "Unique name: a letter, then up to 31 letters, digits, '-' or '_'. Also the p4d server ID.",
        };
        Option<string> releaseOption = new("--release")
        {
            Description = "p4d release, for example r25.1.",
            Required = true,
        };
        Option<int> portOption = new("--port")
        {
            Description = "TCP port p4d listens on, between 1024 and 65535.",
            Required = true,
        };
        Option<string> superUserOption = new("--super-user")
        {
            Description = "Name of the super user created with the server. Its password is asked on the terminal.",
            HelpName = "name",
            Required = true,
        };
        Option<bool> passwordStdinOption = new("--password-stdin")
        {
            Description = "Read the password of the super user from the standard input instead.",
        };
        Option<string?> descriptionOption = new("--description") { Description = "Free text." };
        Option<string?> rootOption = new("--root")
        {
            Description = "Absolute P4ROOT. Defaults to a folder of the instance in the data directory.",
            HelpName = "path",
        };
        Option<bool> caseInsensitiveOption = new("--case-insensitive")
        {
            Description = "Run p4d case-insensitive (-C1). It cannot be changed later.",
        };
        Option<bool> unicodeOption = new("--unicode")
        {
            Description = "Run the server in Unicode mode. It cannot be undone.",
        };
        Option<bool> noAutoStartOption = new("--no-auto-start")
        {
            Description = "Do not start p4d when the daemon starts.",
        };
        Option<bool> noWaitOption = new("--no-wait")
        {
            Description = "Return once the instance is created, without waiting for its provisioning.",
        };

        Command command = new("create", "Create an instance: download its p4d, initialize its server and start it.")
        {
            nameArgument,
            releaseOption,
            portOption,
            superUserOption,
            passwordStdinOption,
            descriptionOption,
            rootOption,
            caseInsensitiveOption,
            unicodeOption,
            noAutoStartOption,
            noWaitOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            string password = await ReadPasswordAsync(
                invocation,
                invocation.GetValue(passwordStdinOption),
                cancellationToken);
            CreateInstanceRequest request = new()
            {
                Name = invocation.GetValue(nameArgument),
                Description = invocation.GetValue(descriptionOption),
                Release = invocation.GetValue(releaseOption),
                Port = invocation.GetValue(portOption),
                CaseSensitive = !invocation.GetValue(caseInsensitiveOption),
                Unicode = invocation.GetValue(unicodeOption),
                AutoStart = !invocation.GetValue(noAutoStartOption),
                RootPath = invocation.GetValue(rootOption),
                SuperUser = new SuperUserRequest { Name = invocation.GetValue(superUserOption), Password = password },
            };

            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.CreateInstanceAsync(request, cancellationToken);

            if (invocation.GetValue(noWaitOption))
            {
                if (invocation.Json)
                {
                    invocation.WriteJson(instance, CliJsonContext.Default.InstanceDto);
                }
                else
                {
                    await invocation.Output.WriteLineAsync(
                        $"Created {instance.Name}. Its provisioning goes on in the daemon: follow it with "
                        + $"'p4sm instance operation {instance.Name} --follow'.");
                }
                return 0;
            }

            await invocation.Progress.WriteLineAsync($"Creating {instance.Name}...");
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
            InstanceDto created = await client.GetInstanceAsync(instance.Id, cancellationToken);
            if (exitCode == 0 && created.State == P4ServerInstanceState.Failed)
            {
                // The daemon restarted during the provisioning, and no longer has its progress.
                await invocation.Error.WriteLineAsync($"Error: {created.LastError ?? "The provisioning failed."}");
                exitCode = CliServices.ErrorExitCode;
            }

            if (invocation.Json)
            {
                invocation.WriteJson(created, CliJsonContext.Default.InstanceDto);
            }
            else if (exitCode == 0)
            {
                await invocation.Output.WriteLineAsync($"{created.Name} is ready on port {created.Port}.");
            }
            return exitCode;
        });
        return command;
    }

    /// <summary>
    /// Reads the password of the super user. It is never taken from an argument, which other users could read in the
    /// list of the processes or in the shell history.
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
        string password = prompter.ReadSecret("Password of the super user: ");
        if (password.Length == 0)
        {
            throw new CliException("The password of the super user cannot be empty.");
        }
        if (prompter.ReadSecret("Confirm the password: ") != password)
        {
            throw new CliException("The passwords do not match.");
        }
        return password;
    }
}
