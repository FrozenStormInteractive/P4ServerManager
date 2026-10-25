using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Creates the super user given at creation, and makes it the only super user of the protections table.
/// </summary>
public sealed class CreateSuperUserStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    ILogger<CreateSuperUserStep> logger) : IOperationStep<ProvisioningContext>
{
    public string Key => "create-super-user";

    public string Title => "Create the super user";

    public async Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        SuperUserCredentials superUser = context.SuperUser
            ?? throw new InvalidOperationException("A created server needs a super user.");
        TimeSpan timeout = ProcessRunnerExtensions.CommandTimeout;
        string p4Path = buildCache.GetP4Path(instance.Build!);
        string clientDirectory = paths.GetClientStateDirectory(instance.Id);
        List<string> connection = ["-p", $"localhost:{instance.Port}", "-u", superUser.Name];
        if (instance.Unicode)
        {
            connection.AddRange(["-C", "utf8"]);
        }

        // Keeps the p4 settings of the account the daemon runs as out of the way.
        Dictionary<string, string> environment = new()
        {
            ["P4CONFIG"] = ".p4config-unused",
            ["P4ENVIRO"] = Path.Combine(clientDirectory, ".p4enviro"),
            ["P4TICKETS"] = Path.Combine(clientDirectory, ".p4tickets"),
            ["P4TRUST"] = Path.Combine(clientDirectory, ".p4trust"),
        };

        // The password goes first: since 2026.1, p4d refuses any other command from a user without one. Setting it
        // creates the user.
        await processRunner.RunCheckedAsync(
            new ProcessCommand(
                p4Path,
                [.. connection, "passwd"],
                $"{superUser.Password}\n{superUser.Password}\n",
                environment,
                timeout),
            cancellationToken);

        // Recent servers refuse P4PASSWD once a password is set: a ticket is needed. It is invalidated right after, so
        // no credential of the super user stays on disk.
        await processRunner.RunCheckedAsync(
            new ProcessCommand(p4Path, [.. connection, "login"], $"{superUser.Password}\n", environment, timeout),
            cancellationToken);
        try
        {
            string userSpec = $"User:\t{superUser.Name}\n\nEmail:\t{superUser.Name}@localhost\n\nFullName:\t{superUser.Name}\n";
            await processRunner.RunCheckedAsync(
                new ProcessCommand(p4Path, [.. connection, "user", "-f", "-i"], userSpec, environment, timeout),
                cancellationToken);

            // Saving the protections table for the first time makes the current user the only super user.
            ProcessResult protections = await processRunner.RunCheckedAsync(
                new ProcessCommand(p4Path, [.. connection, "protect", "-o"], null, environment, timeout),
                cancellationToken);
            await processRunner.RunCheckedAsync(
                new ProcessCommand(
                    p4Path,
                    [.. connection, "protect", "-i"],
                    protections.StandardOutput,
                    environment,
                    timeout),
                cancellationToken);
        }
        finally
        {
            ProcessResult logout = await processRunner.RunAsync(
                new ProcessCommand(p4Path, [.. connection, "logout"], null, environment, timeout),
                CancellationToken.None);
            if (!logout.Succeeded)
            {
                logger.LogWarning("Could not log the super user out of instance {Name}: {Error}",
                    instance.Name, logout.ErrorText);
            }
        }
        return superUser.Name;
    }
}
