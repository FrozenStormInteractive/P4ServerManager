using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

/// <summary>
/// State shared by the upgrade steps of one instance. The instance is pinned to the target build only right before the
/// database is upgraded.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/>. Disposed once the upgrade ends.</param>
/// <param name="instance">The instance, ready, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
/// <param name="release">Release the instance moves to: its latest build is the target, unless one is requested.</param>
/// <param name="wasRunning">Whether p4d ran before the upgrade: it is started again only then.</param>
/// <param name="requestedBuildId">Exact build of the archive the instance moves to, if any.</param>
public sealed class UpgradeContext(
    DaemonDbContext db,
    P4ServerInstance instance,
    P4ServerRelease release,
    bool wasRunning,
    Guid? requestedBuildId = null) : InstanceOperationContext(db, instance), IBuildResolutionContext, IOperationOutcome
{
    /// <summary>
    /// Outcome of an upgrade that moved the instance to another build.
    /// </summary>
    public const string UpgradedOutcome = "upgraded";

    /// <summary>
    /// Outcome of an upgrade of an instance that already ran the latest build: nothing was changed.
    /// </summary>
    public const string UpToDateOutcome = "up-to-date";

    public P4ServerRelease Release { get; } = release;

    public bool WasRunning { get; } = wasRunning;

    /// <summary>
    /// Exact build the instance moves to. Null for the latest build of <see cref="Release"/>.
    /// </summary>
    public Guid? RequestedBuildId { get; } = requestedBuildId;

    public string ReleaseName => Release.Name;

    public bool PinResolvedBuild => false;

    /// <summary>
    /// Build the instance ran before the upgrade.
    /// </summary>
    public P4ServerBuild PreviousBuild { get; } = instance.Build
        ?? throw new ArgumentException("The instance has no p4d build.", nameof(instance));

    /// <summary>
    /// Whether the instance already runs the target build: there is nothing left to do.
    /// </summary>
    public bool UpToDate => Values.TryGet(ResolveBuildStep.ResolvedBuild, out P4ServerBuild? target)
        && target.Id == PreviousBuild.Id;

    public string Outcome => UpToDate ? UpToDateOutcome : UpgradedOutcome;

    /// <summary>
    /// Whether the instance was pinned to the target build for p4d to upgrade the database. The previous build may no
    /// longer open the database from then on.
    /// </summary>
    public bool DatabaseUpgradeStarted => Values.TryGet(UpgradeDatabaseStep.Started, out _);

    /// <summary>
    /// Whether p4d has upgraded the database.
    /// </summary>
    public bool DatabaseUpgraded => Values.TryGet(UpgradeDatabaseStep.Completed, out _);
}
