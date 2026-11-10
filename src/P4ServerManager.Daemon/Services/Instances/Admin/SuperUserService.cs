using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Admin;

/// <summary>
/// The super user account stored for each instance: stores it, checks it, and runs p4 commands as it. The sessions of
/// an instance run one at a time: they share its tickets file, and the logout of one would end the others.
/// </summary>
public sealed class SuperUserService(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    SuperUserVerifier verifier,
    SuperUserPasswordProtector protector,
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    InstanceRuntimeRegistry runtimeRegistry,
    InstanceNotifier notifier,
    TimeProvider timeProvider,
    ILogger<SuperUserService> logger)
{
    private const string UnreadablePasswordMessage =
        "The stored password cannot be decrypted: the keys of the data directory are missing or were replaced. "
        + "Set the super user again.";

    // The lock of a deleted instance is kept: a caller may still wait on it.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> sessionLocks = new();

    /// <summary>
    /// Stores the account of an instance in place of the current one. When the server runs, the account is checked
    /// first, and refused unless it logs in with super access. Otherwise, it is checked once the server runs.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The instance does not exist.</exception>
    /// <exception cref="InstanceConflictException">The instance is not ready, or an operation is in progress.</exception>
    /// <exception cref="SuperUserRejectedException">The server refused the account.</exception>
    public async Task<P4ServerInstance> SetAsync(
        Guid instanceId,
        SuperUserCredentials superUser,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim sessionLock = GetLock(instanceId);
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await FindAsync(db, instanceId, cancellationToken);
            InstanceRuntimeStatus runtimeStatus = runtimeRegistry.Get(instanceId);
            if (instance.State != P4ServerInstanceState.Ready || runtimeStatus.IsTransitional())
            {
                throw new InstanceConflictException("The instance is not ready, or another operation is in progress.");
            }

            SuperUserCheckResult? result = runtimeStatus == InstanceRuntimeStatus.Running
                ? await verifier.CheckAsync(instance, superUser, cancellationToken)
                : null;
            if (result?.Status is SuperUserStatus status && status != SuperUserStatus.Valid)
            {
                throw new SuperUserRejectedException(status, result.Error ?? "The server refused the account.");
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            bool checkedNow = result?.ReachedServer == true;
            InstanceSuperUser stored = instance.SuperUser ??= new InstanceSuperUser
            {
                UserName = superUser.Name,
                ProtectedPassword = string.Empty,
            };
            stored.UserName = superUser.Name;
            stored.ProtectedPassword = protector.Protect(superUser.Password);
            stored.Status = checkedNow ? SuperUserStatus.Valid : SuperUserStatus.Unchecked;
            stored.CheckedAt = checkedNow ? now : null;
            stored.LastError = null;
            stored.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Stored super user {User} of instance {Name} ({Status})",
                superUser.Name, instance.Name, stored.Status);
            await notifier.InstanceChangedAsync(instance);
            return instance;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Checks the stored account of a running instance now, even one already found invalid.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The instance does not exist.</exception>
    /// <exception cref="SuperUserUnavailableException">No account is stored, or the server does not run.</exception>
    /// <exception cref="InstanceOperationException">The server could not be reached.</exception>
    public async Task<P4ServerInstance> CheckAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        SemaphoreSlim sessionLock = GetLock(instanceId);
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance instance = await FindAsync(db, instanceId, cancellationToken);
            if (instance.SuperUser is null)
            {
                throw new SuperUserUnavailableException("No super user is stored for this instance.");
            }
            if (instance.State != P4ServerInstanceState.Ready
                || runtimeRegistry.Get(instanceId) != InstanceRuntimeStatus.Running)
            {
                throw new SuperUserUnavailableException("The server must run to check its super user.");
            }

            SuperUserCheckResult result = await CheckStoredAsync(db, instance, cancellationToken);
            if (!result.ReachedServer)
            {
                throw new InstanceOperationException($"Could not reach the server: {result.Error}");
            }
            return instance;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Checks the stored account of an instance if it can be checked now: the instance is ready and runs, nothing else
    /// acts as its super user, and its account was not already found invalid. A wrong password is not tried again and
    /// again: p4d holds the failed logins against the account.
    /// </summary>
    /// <returns>False when the instance was left as it was.</returns>
    public async Task<bool> TryCheckAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (runtimeRegistry.Get(instanceId) != InstanceRuntimeStatus.Running)
        {
            return false;
        }

        SemaphoreSlim sessionLock = GetLock(instanceId);
        if (!await sessionLock.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            P4ServerInstance? instance = await db.Instances
                .Include(i => i.Build)
                .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
            if (instance?.SuperUser is not { } superUser
                || instance.State != P4ServerInstanceState.Ready
                || superUser.Status is SuperUserStatus.InvalidCredentials or SuperUserStatus.PasswordExpired)
            {
                return false;
            }

            SuperUserCheckResult result = await CheckStoredAsync(db, instance, cancellationToken);
            return result.ReachedServer;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Logs in to a running instance as its stored super user, runs <paramref name="action"/>, and logs out.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The instance does not exist.</exception>
    /// <exception cref="SuperUserUnavailableException">
    /// No usable account is stored, or the server does not run.
    /// </exception>
    /// <exception cref="InstanceOperationException">A p4 command failed.</exception>
    public async Task<T> RunAsSuperUserAsync<T>(
        Guid instanceId,
        Func<P4AdminSession, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim sessionLock = GetLock(instanceId);
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            P4ServerInstance instance;
            await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
            {
                instance = await db.Instances
                    .AsNoTracking()
                    .Include(i => i.Build)
                    .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Instance {instanceId} does not exist.");
            }

            SuperUserCredentials superUser = GetStoredCredentials(instance);
            if (instance.State != P4ServerInstanceState.Ready
                || runtimeRegistry.Get(instanceId) != InstanceRuntimeStatus.Running)
            {
                throw new SuperUserUnavailableException("The server of this instance does not run.");
            }

            P4AdminSession session = await P4AdminSession.OpenAsync(
                buildCache,
                processRunner,
                paths,
                logger,
                instance,
                superUser,
                cancellationToken);
            return await session.RunLoggedInAsync(() => action(session, cancellationToken), cancellationToken);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Reads the stored account of an instance, for an operation that runs p4 commands as it.
    /// </summary>
    /// <exception cref="SuperUserUnavailableException">
    /// No account is stored, it was found invalid, or its password cannot be decrypted.
    /// </exception>
    public SuperUserCredentials GetStoredCredentials(P4ServerInstance instance)
    {
        if (instance.SuperUser is not { Status: SuperUserStatus.Valid or SuperUserStatus.Unchecked } stored)
        {
            throw new SuperUserUnavailableException(instance.SuperUser is null
                ? "No super user is stored for this instance."
                : "The stored super user of this instance is not usable: change it first.");
        }

        string password = Unprotect(stored) ?? throw new SuperUserUnavailableException(UnreadablePasswordMessage);
        return new SuperUserCredentials(stored.UserName, password);
    }

    // Checks the account and saves what the server said of it. An unreachable server leaves it as it was.
    private async Task<SuperUserCheckResult> CheckStoredAsync(
        DaemonDbContext db,
        P4ServerInstance instance,
        CancellationToken cancellationToken)
    {
        InstanceSuperUser stored = instance.SuperUser!;
        string? password = Unprotect(stored);
        SuperUserCheckResult result = password is null
            ? new SuperUserCheckResult(SuperUserStatus.InvalidCredentials, UnreadablePasswordMessage)
            : await verifier.CheckAsync(instance, new SuperUserCredentials(stored.UserName, password), cancellationToken);
        if (result.Status is not SuperUserStatus status)
        {
            logger.LogDebug("Could not check the super user of instance {Name}: {Error}", instance.Name, result.Error);
            return result;
        }

        SuperUserStatus previous = stored.Status;
        stored.Status = status;
        stored.LastError = result.Error;
        stored.CheckedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        if (status != previous)
        {
            if (status == SuperUserStatus.Valid)
            {
                logger.LogInformation("Super user {User} of instance {Name} is valid", stored.UserName, instance.Name);
            }
            else
            {
                logger.LogWarning("Super user {User} of instance {Name} is not usable ({Status}): {Error}",
                    stored.UserName, instance.Name, status, stored.LastError);
            }
        }

        // The check takes a while, and an operation may have saved the instance meanwhile, such as the last error of an
        // operation that started p4d and so asked for this check. The rest of the instance is read again, so that this
        // update does not hide it from the clients.
        await db.Entry(instance).ReloadAsync(cancellationToken);
        await notifier.InstanceChangedAsync(instance);
        return result;
    }

    private string? Unprotect(InstanceSuperUser stored)
    {
        try
        {
            return protector.Unprotect(stored.ProtectedPassword);
        }
        catch (CryptographicException exception)
        {
            logger.LogWarning(exception, "Could not decrypt the password of super user {User}", stored.UserName);
            return null;
        }
    }

    private static async Task<P4ServerInstance> FindAsync(
        DaemonDbContext db,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        return await db.Instances
            .Include(i => i.Build)
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken)
            ?? throw new KeyNotFoundException($"Instance {instanceId} does not exist.");
    }

    private SemaphoreSlim GetLock(Guid instanceId)
    {
        return sessionLocks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
    }
}
