using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances;

namespace P4ServerManager.Daemon.Services.Backups;

/// <summary>
/// Saves where a backup is and pushes it to the clients.
/// </summary>
public sealed class BackupRunNotifier(InstanceNotifier notifier)
{
    /// <summary>
    /// Saves the context of the run, which tracks it, then pushes the run.
    /// </summary>
    public async Task SaveAsync(Database.DaemonDbContext db, BackupRun run)
    {
        await db.SaveChangesAsync(CancellationToken.None);
        await notifier.BackupChangedAsync(run.ToDto());
    }
}
