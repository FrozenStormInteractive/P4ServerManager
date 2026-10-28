using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Runs p4 commands on an instance as its super user, with p4 settings of its own in the folder of the instance: the
/// settings of the account the daemon runs as are never read nor changed. The password only goes through the
/// standard input.
/// </summary>
public sealed class P4AdminSession
{
    private readonly IProcessRunner processRunner;
    private readonly ILogger logger;
    private readonly P4ServerInstance instance;
    private readonly SuperUserCredentials superUser;
    private readonly string p4Path;
    private readonly List<string> connection;
    private readonly Dictionary<string, string> environment;

    private P4AdminSession(
        IProcessRunner processRunner,
        ILogger logger,
        P4ServerInstance instance,
        SuperUserCredentials superUser,
        string p4Path,
        string clientDirectory)
    {
        this.processRunner = processRunner;
        this.logger = logger;
        this.instance = instance;
        this.superUser = superUser;
        this.p4Path = p4Path;

        // The character set is always given: otherwise p4 falls back to P4CHARSET from the environment or, on Windows,
        // the registry, and a Unicode one makes a server not in Unicode mode refuse every command.
        connection =
        [
            "-p", $"{(instance.Ssl ? "ssl:" : string.Empty)}localhost:{instance.Port}",
            "-u", superUser.Name,
            "-C", instance.Unicode ? "utf8" : "none",
        ];
        environment = new Dictionary<string, string>
        {
            ["P4CONFIG"] = ".p4config-unused",
            ["P4ENVIRO"] = Path.Combine(clientDirectory, ".p4enviro"),
            ["P4TICKETS"] = Path.Combine(clientDirectory, ".p4tickets"),
            ["P4TRUST"] = Path.Combine(clientDirectory, ".p4trust"),
        };
    }

    /// <summary>
    /// Opens a session on the running p4d of the instance. With SSL, its certificate is trusted first: the daemon
    /// talks to it on the same machine.
    /// </summary>
    public static async Task<P4AdminSession> OpenAsync(
        IP4ServerBuildCache buildCache,
        IProcessRunner processRunner,
        DaemonPaths paths,
        ILogger logger,
        ProvisioningContext context,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        SuperUserCredentials superUser = context.SuperUser
            ?? throw new InvalidOperationException("A created server needs a super user.");
        P4AdminSession session = new(
            processRunner,
            logger,
            instance,
            superUser,
            buildCache.GetP4Path(instance.Build!),
            paths.GetClientStateDirectory(instance.Id));
        if (instance.Ssl)
        {
            await session.RunAsync(["trust", "-f", "-y"], cancellationToken: cancellationToken);
        }
        return session;
    }

    /// <summary>
    /// Sets the password of the super user, which creates it. Since 2026.1, p4d refuses any other command from a user
    /// without a password. Older releases only set the password of an existing user: when it is refused, the user is
    /// created from <see cref="GetUserSpec"/> first.
    /// </summary>
    /// <returns>
    /// Whether the user was saved from <see cref="GetUserSpec"/>. Before 2026.1, saving it again at a low security
    /// level would clear the password: the spec has no <c>Password</c> field.
    /// </returns>
    public async Task<bool> SetPasswordAsync(CancellationToken cancellationToken)
    {
        string passwords = $"{superUser.Password}\n{superUser.Password}\n";
        ProcessResult result = await TryRunAsync(["passwd"], passwords, cancellationToken);
        if (result.Succeeded)
        {
            return false;
        }

        logger.LogDebug("Creating super user {User} before setting its password on instance {Name}: {Error}",
            superUser.Name, instance.Name, result.ErrorText);
        await RunAsync(["user", "-f", "-i"], GetUserSpec(), cancellationToken);
        await RunAsync(["passwd"], passwords, cancellationToken);
        return true;
    }

    /// <summary>
    /// The user spec of the super user.
    /// </summary>
    public string GetUserSpec()
    {
        return $"User:\t{superUser.Name}\n\nEmail:\t{superUser.Name}@localhost\n\nFullName:\t{superUser.Name}\n";
    }

    /// <summary>
    /// Logs the super user in, runs <paramref name="action"/>, and logs it out. Recent servers refuse P4PASSWD once a
    /// password is set: a ticket is needed. It is invalidated right after, so no credential of the super user stays on
    /// disk.
    /// </summary>
    public async Task<T> RunLoggedInAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await RunAsync(["login"], $"{superUser.Password}\n", cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            ProcessResult logout = await TryRunAsync(["logout"], cancellationToken: CancellationToken.None);
            if (!logout.Succeeded)
            {
                logger.LogWarning("Could not log the super user out of instance {Name}: {Error}",
                    instance.Name, logout.ErrorText);
            }
        }
    }

    /// <summary>
    /// Runs a p4 command, and throws an <see cref="InstanceOperationException"/> when it fails.
    /// </summary>
    public Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        return processRunner.RunCheckedAsync(CreateCommand(arguments, standardInput), cancellationToken);
    }

    /// <summary>
    /// Runs a p4 command, and lets the caller handle its failure.
    /// </summary>
    public Task<ProcessResult> TryRunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        return processRunner.RunAsync(CreateCommand(arguments, standardInput), cancellationToken);
    }

    private ProcessCommand CreateCommand(IReadOnlyList<string> arguments, string? standardInput)
    {
        return new ProcessCommand(
            p4Path,
            [.. connection, .. arguments],
            standardInput,
            environment,
            ProcessRunnerExtensions.CommandTimeout);
    }
}
