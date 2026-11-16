using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Output;
using P4ServerManager.Cli.Prompts;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Users;

/// <summary>
/// The <c>user</c> command: the accounts that sign in to the web interface. The CLI itself needs none.
/// </summary>
internal static class UserCommand
{
    public static Command Create(CliServices services)
    {
        Command command = new(
            "user",
            "Manage the accounts that sign in to the web interface. Every account has all rights on the servers; only "
            + "administrators manage the accounts. The CLI needs no account: whoever can open the socket of the daemon "
            + "is trusted, as an administrator.");
        command.Aliases.Add("users");
        command.Subcommands.Add(CreateList(services));
        command.Subcommands.Add(CreateCreate(services));
        command.Subcommands.Add(CreateSetPassword(services));
        command.Subcommands.Add(CreateSetAdministrator(services, isAdministrator: true));
        command.Subcommands.Add(CreateSetAdministrator(services, isAdministrator: false));
        command.Subcommands.Add(CreateDelete(services));
        return command;
    }

    private static Command CreateList(CliServices services)
    {
        Command command = new("list", "List the accounts.");
        command.Aliases.Add("ls");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            IReadOnlyList<UserDto> users = await client.GetUsersAsync(cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(users, CliJsonContext.Default.IReadOnlyListUserDto);
                return 0;
            }
            if (users.Count == 0)
            {
                await invocation.Output.WriteLineAsync(
                    "No accounts. Create the first one with 'p4sm user create', or from the web interface.");
                return 0;
            }

            TableWriter table = new("NAME", "ADMIN", "CREATED", "LAST SIGN-IN");
            foreach (UserDto user in users)
            {
                table.AddRow(
                    user.UserName,
                    user.IsAdministrator ? "yes" : "no",
                    BuildFormatting.FormatDate(user.CreatedAt),
                    BuildFormatting.FormatDate(user.LastSignInAt));
            }
            table.Write(invocation.Output);
            return 0;
        });
        return command;
    }

    private static Command CreateCreate(CliServices services)
    {
        Argument<string> nameArgument = CreateNameArgument();
        Option<bool> passwordStdinOption = CreatePasswordStdinOption();
        Option<bool> adminOption = new("--admin")
        {
            Description = "Make the account an administrator, who manages the accounts. The first account always is one.",
        };
        Command command = new("create", "Add an account, such as the first one after the installation.")
        {
            nameArgument,
            passwordStdinOption,
            adminOption,
        };
        command.Aliases.Add("add");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            string password = await ReadPasswordAsync(
                invocation,
                invocation.GetValue(passwordStdinOption),
                cancellationToken);

            using DaemonClient client = invocation.CreateClient();
            UserDto user = await client.CreateUserAsync(
                new CreateUserRequest
                {
                    UserName = invocation.GetValue(nameArgument),
                    Password = password,
                    IsAdministrator = invocation.GetValue(adminOption),
                },
                cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(user, CliJsonContext.Default.UserDto);
            }
            else
            {
                await invocation.Output.WriteLineAsync(
                    user.IsAdministrator
                        ? $"Created administrator account {user.UserName}."
                        : $"Created account {user.UserName}.");
            }
            return 0;
        });
        return command;
    }

    private static Command CreateSetPassword(CliServices services)
    {
        Argument<string> nameArgument = CreateNameArgument();
        Option<bool> passwordStdinOption = CreatePasswordStdinOption();
        Command command = new(
            "set-password",
            "Set a new password on an account, such as when its user forgot it. Its sessions end.")
        {
            nameArgument,
            passwordStdinOption,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            string password = await ReadPasswordAsync(
                invocation,
                invocation.GetValue(passwordStdinOption),
                cancellationToken);

            using DaemonClient client = invocation.CreateClient();
            UserDto user = await client.ResolveUserAsync(invocation.GetValue(nameArgument), cancellationToken);
            await client.SetUserPasswordAsync(
                user.Id,
                new SetUserPasswordRequest { Password = password },
                cancellationToken);

            if (!invocation.Json)
            {
                await invocation.Output.WriteLineAsync($"Set the password of {user.UserName}.");
            }
            return 0;
        });
        return command;
    }

    private static Command CreateSetAdministrator(CliServices services, bool isAdministrator)
    {
        Argument<string> nameArgument = CreateNameArgument();
        Command command = isAdministrator
            ? new Command("grant-admin", "Make an account an administrator, who manages the accounts.")
            : new Command(
                "revoke-admin",
                "Take back the administrator right of an account. The last administrator keeps it.");
        command.Arguments.Add(nameArgument);

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            UserDto user = await client.ResolveUserAsync(invocation.GetValue(nameArgument), cancellationToken);
            user = await client.SetUserAdministratorAsync(
                user.Id,
                new SetUserAdministratorRequest { IsAdministrator = isAdministrator },
                cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(user, CliJsonContext.Default.UserDto);
            }
            else
            {
                await invocation.Output.WriteLineAsync(
                    isAdministrator
                        ? $"{user.UserName} is an administrator."
                        : $"{user.UserName} is no longer an administrator.");
            }
            return 0;
        });
        return command;
    }

    private static Command CreateDelete(CliServices services)
    {
        Argument<string> nameArgument = CreateNameArgument();
        Option<bool> yesOption = new("--yes", "-y") { Description = "Delete without asking for a confirmation." };
        Command command = new("delete", "Delete an account. Its sessions end. The last administrator cannot be deleted.")
        {
            nameArgument,
            yesOption,
        };
        command.Aliases.Add("rm");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            UserDto user = await client.ResolveUserAsync(invocation.GetValue(nameArgument), cancellationToken);

            if (!invocation.GetValue(yesOption))
            {
                IPrompter prompter = invocation.Environment.Prompter;
                if (!prompter.IsInteractive)
                {
                    throw new CliException("Deleting an account asks for a confirmation. Without a terminal, pass --yes.");
                }
                if (!prompter.Confirm($"Delete the account {user.UserName}?"))
                {
                    await invocation.Error.WriteLineAsync("Nothing was deleted.");
                    return CliServices.ErrorExitCode;
                }
            }

            await client.DeleteUserAsync(user.Id, cancellationToken);
            if (!invocation.Json)
            {
                await invocation.Output.WriteLineAsync($"Deleted account {user.UserName}.");
            }
            return 0;
        });
        return command;
    }

    private static Argument<string> CreateNameArgument()
    {
        return new Argument<string>("name") { Description = "User name of the account." };
    }

    private static Option<bool> CreatePasswordStdinOption()
    {
        return new Option<bool>("--password-stdin")
        {
            Description = "Read the password from the standard input instead of the terminal.",
        };
    }

    private static async Task<string> ReadPasswordAsync(
        CliInvocation invocation,
        bool fromStandardInput,
        CancellationToken cancellationToken)
    {
        string password;
        if (fromStandardInput)
        {
            password = await invocation.Environment.Input.ReadLineAsync(cancellationToken)
                ?? throw new CliException("No password was read from the standard input.");
        }
        else
        {
            IPrompter prompter = invocation.Environment.Prompter;
            if (!prompter.IsInteractive)
            {
                throw new CliException(
                    "The password is asked on a terminal. Without one, pipe it with --password-stdin.");
            }
            password = prompter.ReadSecret("Password: ");
            if (UserPassword.IsValid(password) && prompter.ReadSecret("Confirm the password: ") != password)
            {
                throw new CliException("The passwords do not match.");
            }
        }

        return UserPassword.IsValid(password) ? password : throw new CliException(UserPassword.Requirement);
    }
}
