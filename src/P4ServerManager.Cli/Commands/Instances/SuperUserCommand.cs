using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Instances;

/// <summary>
/// The <c>super-user</c> command: the super user account the daemon stores to administer an instance.
/// </summary>
internal static class SuperUserCommand
{
    public static Command Create(CliServices services)
    {
        Command command = new(
            "super-user",
            "Manage the super user account the daemon stores, encrypted, to administer an instance. The daemon checks "
            + "it regularly: it must log in and have super access.");
        command.Subcommands.Add(CreateSet(services));
        command.Subcommands.Add(CreateCheck(services));
        return command;
    }

    private static Command CreateSet(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Option<string> userOption = new("--user")
        {
            Description = "P4 user name of the account, with super access.",
            Required = true,
        };
        Option<bool> passwordStdinOption = new("--password-stdin")
        {
            Description = "Read the password from the standard input instead of the terminal.",
        };
        Command command = new(
            "set",
            "Store another account, or the new password of the same account after it was changed in P4. Nothing "
            + "changes in P4. When the server runs, the account is checked first and refused unless it logs in with "
            + "super access.")
        {
            instanceArgument,
            userOption,
            passwordStdinOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            string password = await ReadPasswordAsync(
                invocation,
                invocation.GetValue(passwordStdinOption),
                cancellationToken);

            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);
            InstanceDto updated = await client.SetSuperUserAsync(
                instance.Id,
                new SetSuperUserRequest { UserName = invocation.GetValue(userOption), Password = password },
                cancellationToken);

            SuperUserDto superUser = updated.SuperUser!;
            return await WriteAsync(
                invocation,
                updated,
                superUser.Status == SuperUserStatus.Valid
                    ? $"Stored super user {superUser.UserName} of {updated.Name}: it logs in with super access."
                    : $"Stored super user {superUser.UserName} of {updated.Name}. It is checked once the server runs.");
        });
        return command;
    }

    private static Command CreateCheck(CliServices services)
    {
        Argument<string> instanceArgument = InstanceArgument.Create();
        Command command = new(
            "check",
            "Check the stored super user of a running instance now, even one already found invalid.")
        {
            instanceArgument,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            InstanceDto instance = await client.ResolveInstanceAsync(
                invocation.GetValue(instanceArgument),
                cancellationToken);
            InstanceDto updated = await client.CheckSuperUserAsync(instance.Id, cancellationToken);

            SuperUserDto superUser = updated.SuperUser!;
            int exitCode = await WriteAsync(
                invocation,
                updated,
                superUser.Status == SuperUserStatus.Valid
                    ? $"Super user {superUser.UserName} of {updated.Name} logs in with super access."
                    : $"Super user {superUser.UserName} of {updated.Name} is not usable ({superUser.Status}): "
                      + $"{superUser.Error}");
            return superUser.Status == SuperUserStatus.Valid ? exitCode : 1;
        });
        return command;
    }

    // The password already exists in P4: it has no strength rule here.
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

    private static async Task<int> WriteAsync(CliInvocation invocation, InstanceDto instance, string message)
    {
        if (invocation.Json)
        {
            invocation.WriteJson(instance, CliJsonContext.Default.InstanceDto);
        }
        else
        {
            await invocation.Output.WriteLineAsync(message);
        }
        return 0;
    }
}
