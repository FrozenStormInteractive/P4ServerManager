using System.ComponentModel;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Admin;

/// <summary>
/// Checks that an account logs in to a running server and has super access, with <c>p4 login</c> then
/// <c>p4 protects -m</c>. The ticket is removed right after.
/// </summary>
public sealed class SuperUserVerifier(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    ILogger<SuperUserVerifier> logger)
{
    // What p4 prints when it does not reach the server, as opposed to a refused login.
    private static readonly string[] ConnectionErrors =
    [
        "Connect to server failed",
        "TCP connect to",
        "SSL connect to",
        "Connection refused",
        "Partner exited unexpectedly",
    ];

    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    /// <param name="superUser">The account to check.</param>
    /// <param name="cancellationToken">Aborts the check.</param>
    public async Task<SuperUserCheckResult> CheckAsync(
        P4ServerInstance instance,
        SuperUserCredentials superUser,
        CancellationToken cancellationToken)
    {
        try
        {
            P4AdminSession session = await P4AdminSession.OpenAsync(
                buildCache, processRunner, paths, logger, instance, superUser, cancellationToken);
            ProcessResult login = await session.TryRunAsync(["login"], $"{superUser.Password}\n", cancellationToken);
            if (!login.Succeeded)
            {
                return ReadLoginFailure(login.ErrorText);
            }

            try
            {
                ProcessResult protects = await session.TryRunAsync(
                    ["-ztag", "protects", "-m"],
                    cancellationToken: cancellationToken);
                if (!protects.Succeeded)
                {
                    return IsConnectionError(protects.ErrorText)
                        ? SuperUserCheckResult.Unreachable(protects.ErrorText)
                        : new SuperUserCheckResult(SuperUserStatus.InsufficientAccess, protects.ErrorText);
                }

                string? access = ReadMaxPermission(protects.StandardOutput);
                return access == "super"
                    ? SuperUserCheckResult.Valid
                    : new SuperUserCheckResult(
                        SuperUserStatus.InsufficientAccess,
                        $"The user {superUser.Name} has {access ?? "no"} access: super access is needed.");
            }
            finally
            {
                ProcessResult logout = await session.TryRunAsync(["logout"], cancellationToken: CancellationToken.None);
                if (!logout.Succeeded)
                {
                    logger.LogWarning("Could not log the super user out of instance {Name}: {Error}",
                        instance.Name, logout.ErrorText);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InstanceOperationException
                                              or TimeoutException
                                              or IOException
                                              or Win32Exception)
        {
            // p4 could not run, or the server did not answer in time, such as while it stops.
            return SuperUserCheckResult.Unreachable(exception.Message);
        }
    }

    private static SuperUserCheckResult ReadLoginFailure(string error)
    {
        if (IsConnectionError(error))
        {
            return SuperUserCheckResult.Unreachable(error);
        }

        // For example "Your password has expired, please change your password."
        return error.Contains("expired", StringComparison.OrdinalIgnoreCase)
            ? new SuperUserCheckResult(SuperUserStatus.PasswordExpired, error)
            : new SuperUserCheckResult(SuperUserStatus.InvalidCredentials, error);
    }

    private static bool IsConnectionError(string error)
    {
        return ConnectionErrors.Any(e => error.Contains(e, StringComparison.OrdinalIgnoreCase));
    }

    // Tagged output of "p4 protects -m": "... permMax super".
    private static string? ReadMaxPermission(string output)
    {
        const string prefix = "... permMax ";
        return output
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..].Trim())
            .FirstOrDefault();
    }
}

/// <param name="Status">Status of the account. Null when the server could not be reached: it tells nothing of it.</param>
/// <param name="Error">Why the account is not valid, or why the server could not be reached.</param>
public sealed record SuperUserCheckResult(SuperUserStatus? Status, string? Error)
{
    public static readonly SuperUserCheckResult Valid = new(SuperUserStatus.Valid, null);

    public bool ReachedServer => Status is not null;

    public static SuperUserCheckResult Unreachable(string error)
    {
        return new SuperUserCheckResult(null, error);
    }
}
