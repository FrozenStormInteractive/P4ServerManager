using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Services.Authentication;

/// <summary>
/// The accounts of the web interface. The passwords are hashed with <see cref="PasswordHasher{TUser}"/> (salted
/// PBKDF2), never stored. Once accounts exist, at least one of them is an administrator.
/// </summary>
public sealed class UserService(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<UserService> logger)
{
    private readonly PasswordHasher<User> hasher = new();

    // Serializes the changes that depend on the other accounts: two first accounts, or no administrator left.
    private readonly SemaphoreSlim writeLock = new(1, 1);

    // Protects against the enumeration of the accounts by timing. Checking a password costs a PBKDF2 hash, tens of
    // milliseconds on purpose: a sign-in that skipped it for an unknown name would answer much faster than one with a
    // wrong password, and tell an attacker which user names exist, even with the same error message. So a sign-in with
    // an unknown name checks its password against this hash, of a random value that no password matches, and drops the
    // result: both cases cost the same. Computed once, on the first sign-in with an unknown name.
    private readonly Lazy<string> dummyHash = new(() => new PasswordHasher<User>().HashPassword(
        new User { UserName = string.Empty, PasswordHash = string.Empty },
        Guid.NewGuid().ToString()));

    /// <summary>
    /// Whether at least one account exists.
    /// </summary>
    public async Task<bool> AnyAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AnyAsync(cancellationToken);
    }

    /// <summary>
    /// The accounts, by name.
    /// </summary>
    public async Task<List<User>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(cancellationToken);
    }

    public async Task<User?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    /// <summary>
    /// Whether an account exists and is an administrator.
    /// </summary>
    public async Task<bool> IsAdministratorAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AnyAsync(u => u.Id == id && u.IsAdministrator, cancellationToken);
    }

    /// <summary>
    /// The security stamp of an account, or null when it no longer exists.
    /// </summary>
    public async Task<Guid?> GetSecurityStampAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users
            .Where(u => u.Id == id)
            .Select(u => (Guid?)u.SecurityStamp)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Adds an account.
    /// </summary>
    /// <param name="userName">Name of the account, already checked.</param>
    /// <param name="password">Password of the account, already checked.</param>
    /// <param name="isAdministrator">
    /// Whether the user manages the accounts. The first account is always an administrator.
    /// </param>
    /// <param name="firstOnly">Only adds the account when none exists.</param>
    /// <param name="cancellationToken">Aborts the creation.</param>
    /// <exception cref="UserConflictException">
    /// Another account has this name, or <paramref name="firstOnly"/> is set and an account exists.
    /// </exception>
    public async Task<User> CreateAsync(
        string userName,
        string password,
        bool isAdministrator,
        bool firstOnly,
        CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            if (firstOnly && await db.Users.AnyAsync(cancellationToken))
            {
                throw new UserConflictException("The first account already exists. Sign in instead.");
            }
            // The user names are compared with the NOCASE collation of their column.
            if (await db.Users.AnyAsync(u => u.UserName == userName, cancellationToken))
            {
                throw new UserConflictException($"An account named {userName} already exists.");
            }
            // Otherwise, nobody would manage the accounts from the web interface.
            isAdministrator |= !await db.Users.AnyAsync(cancellationToken);

            User user = new()
            {
                Id = Guid.NewGuid(),
                UserName = userName,
                PasswordHash = string.Empty,
                SecurityStamp = Guid.NewGuid(),
                IsAdministrator = isAdministrator,
                CreatedAt = timeProvider.GetUtcNow(),
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                user.IsAdministrator ? "Created administrator account {User}" : "Created account {User}",
                user.UserName);
            return user;
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>
    /// Checks a user name and a password, and records the sign-in when they match.
    /// </summary>
    /// <returns>The account, or null when no account has this name and this password.</returns>
    public async Task<User?> VerifyAsync(string userName, string password, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        User? user = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken);
        if (user is null)
        {
            // Spends the time of a real check, so that an unknown name cannot be told from a wrong password (see
            // dummyHash). The result is ignored: no password matches this hash.
            hasher.VerifyHashedPassword(
                new User { UserName = userName, PasswordHash = string.Empty },
                dummyHash.Value,
                password);
            return null;
        }

        PasswordVerificationResult result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
        }
        user.LastSignInAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    /// <summary>
    /// Records that a user signed in without a check of its password, such as right after it created its account.
    /// </summary>
    public async Task<User> RecordSignInAsync(User user, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.Users.Attach(user);
        user.LastSignInAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    /// <summary>
    /// Changes the password of an account after checking its current one. The other sessions of the account end.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The account no longer exists.</exception>
    /// <exception cref="UserPasswordMismatchException">The current password is not the right one.</exception>
    public async Task<User> ChangePasswordAsync(
        Guid id,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        User user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
        {
            throw new UserPasswordMismatchException();
        }
        return await SavePasswordAsync(db, user, newPassword, cancellationToken);
    }

    /// <summary>
    /// Sets a new password on an account, without its current one. The sessions of the account end.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No account has this identifier.</exception>
    public async Task<User> SetPasswordAsync(Guid id, string password, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        User user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException();
        return await SavePasswordAsync(db, user, password, cancellationToken);
    }

    /// <summary>
    /// Names an account administrator, or takes this right back. The right is checked on each request, so the
    /// sessions of the account go on with the new right.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No account has this identifier.</exception>
    /// <exception cref="UserConflictException">
    /// The account is the last administrator, and would lose the right.
    /// </exception>
    public async Task<User> SetAdministratorAsync(Guid id, bool isAdministrator, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            User user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException();
            if (user.IsAdministrator == isAdministrator)
            {
                return user;
            }
            if (!isAdministrator)
            {
                await EnsureOtherAdministratorAsync(db, id, cancellationToken);
            }

            user.IsAdministrator = isAdministrator;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                isAdministrator ? "Made account {User} an administrator" : "Account {User} is no longer an administrator",
                user.UserName);
            return user;
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>
    /// Deletes an account. Its sessions end.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No account has this identifier.</exception>
    /// <exception cref="UserConflictException">The account is the last administrator.</exception>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            User user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException();
            if (user.IsAdministrator)
            {
                await EnsureOtherAdministratorAsync(db, id, cancellationToken);
            }
            db.Users.Remove(user);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Deleted account {User}", user.UserName);
        }
        finally
        {
            writeLock.Release();
        }
    }

    // Without an administrator, nobody could manage the accounts from the web interface. The last account is always
    // the last administrator, so it cannot be deleted either.
    private static async Task EnsureOtherAdministratorAsync(
        DaemonDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id != id && u.IsAdministrator, cancellationToken))
        {
            throw new UserConflictException(
                "This is the last administrator: nobody could manage the accounts from the web interface without it. "
                + "Make another account an administrator first.");
        }
    }

    private async Task<User> SavePasswordAsync(
        DaemonDbContext db,
        User user,
        string password,
        CancellationToken cancellationToken)
    {
        user.PasswordHash = hasher.HashPassword(user, password);
        user.SecurityStamp = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Changed the password of account {User}", user.UserName);
        return user;
    }
}
