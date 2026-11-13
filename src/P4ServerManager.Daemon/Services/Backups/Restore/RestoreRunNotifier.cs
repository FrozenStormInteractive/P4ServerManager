using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances;

namespace P4ServerManager.Daemon.Services.Backups.Restore;

/// <summary>
/// Saves where a restore is and pushes it to the clients.
/// </summary>
public sealed class RestoreRunNotifier(InstanceNotifier notifier)
{
    public async Task SaveAsync(DaemonDbContext db, RestoreRun run)
    {
        await db.SaveChangesAsync(CancellationToken.None);
        await notifier.RestoreChangedAsync(run.ToDto());
    }
}
