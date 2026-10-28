using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Database;

public static class DaemonDbContextExtensions
{
    /// <summary>
    /// Returns the policy of the build archive, tracked. Until it is saved once, a new policy with the defaults is
    /// added to the context, and saved with its next changes.
    /// </summary>
    public static async Task<BuildArchivePolicy> GetBuildArchivePolicyAsync(
        this DaemonDbContext db,
        CancellationToken cancellationToken)
    {
        BuildArchivePolicy? policy = await db.BuildArchivePolicies
            .FirstOrDefaultAsync(p => p.Id == BuildArchivePolicy.SingletonId, cancellationToken);
        if (policy is null)
        {
            policy = new BuildArchivePolicy();
            db.BuildArchivePolicies.Add(policy);
        }
        return policy;
    }
}
