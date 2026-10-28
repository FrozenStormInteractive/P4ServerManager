using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Hubs;

namespace P4ServerManager.Daemon.Services.Builds;

/// <summary>
/// Pushes the changes of the p4d builds to the SignalR clients. A failed push is logged and never breaks the operation
/// that triggered it.
/// </summary>
public sealed class BuildNotifier(
    IHubContext<BuildsHub, IBuildsClient> hubContext,
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    ILogger<BuildNotifier> logger)
{
    /// <summary>
    /// Reads the build back, with the instances that use it, and pushes it.
    /// </summary>
    public async Task BuildChangedAsync(Guid buildId)
    {
        try
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync();
            P4ServerBuild? build = await db.Builds
                .AsNoTracking()
                .Include(b => b.Instances)
                .FirstOrDefaultAsync(b => b.Id == buildId);
            if (build is not null)
            {
                await hubContext.Clients.All.BuildUpdated(build.ToDto());
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not push the update of build {Id}", buildId);
        }
    }

    public async Task BuildDeletedAsync(Guid buildId)
    {
        try
        {
            await hubContext.Clients.All.BuildDeleted(buildId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not push the deletion of build {Id}", buildId);
        }
    }

    public async Task ArchiveStatusChangedAsync(BuildArchiveStatusDto status)
    {
        try
        {
            await hubContext.Clients.All.ArchiveStatusChanged(status);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not push the status of the build archive");
        }
    }
}
