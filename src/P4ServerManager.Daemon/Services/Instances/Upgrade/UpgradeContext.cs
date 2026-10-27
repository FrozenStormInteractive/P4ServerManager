using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

/// <summary>
/// State shared by the upgrade steps of one instance.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/>. Disposed once the upgrade ends.</param>
/// <param name="instance">The instance, ready, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
/// <param name="release">Release the instance moves to: its latest build is the target.</param>
/// <param name="wasRunning">Whether p4d ran before the upgrade: it is started again only then.</param>
public sealed class UpgradeContext(
    DaemonDbContext db,
    P4ServerInstance instance,
    P4ServerRelease release,
    bool wasRunning) : IOperationOutcome
{
    /// <summary>
    /// Outcome of an upgrade that moved the instance to another build.
    /// </summary>
    public const string UpgradedOutcome = "upgraded";

    /// <summary>
    /// Outcome of an upgrade of an instance that already ran the latest build: nothing was changed.
    /// </summary>
    public const string UpToDateOutcome = "up-to-date";

    public DaemonDbContext Db { get; } = db;

    public P4ServerInstance Instance { get; } = instance;

    public P4ServerRelease Release { get; } = release;

    public bool WasRunning { get; } = wasRunning;

    /// <summary>
    /// Build the instance ran before the upgrade.
    /// </summary>
    public P4ServerBuild PreviousBuild { get; } = instance.Build
        ?? throw new ArgumentException("The instance has no p4d build.", nameof(instance));

    /// <summary>
    /// Latest build of <see cref="Release"/>, once downloaded.
    /// </summary>
    public P4ServerBuild? TargetBuild { get; set; }

    /// <summary>
    /// Set when the instance already runs the latest build of <see cref="Release"/>: there is nothing left to do.
    /// </summary>
    public bool UpToDate { get; set; }

    public string Outcome => UpToDate ? UpToDateOutcome : UpgradedOutcome;

    /// <summary>
    /// Set once the instance is pinned to the target build, right before p4d upgrades the database. The previous
    /// build may no longer open the database from then on.
    /// </summary>
    public bool DatabaseUpgradeStarted { get; set; }

    /// <summary>
    /// Set once p4d has upgraded the database.
    /// </summary>
    public bool DatabaseUpgraded { get; set; }
}
