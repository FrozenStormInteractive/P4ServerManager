using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Steps;

namespace P4ServerManager.Daemon.Services.Instances.UnicodeConversion;

/// <summary>
/// State shared by the steps of the conversion of one instance to Unicode mode.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/>. Disposed once the conversion ends.</param>
/// <param name="instance">The instance, ready, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
/// <param name="wasRunning">Whether p4d ran before the conversion: it is started again only then.</param>
/// <param name="checkpointPath">
/// Checkpoint saved before the conversion, the only way back. Each attempt has its own file, so a retry never replaces
/// an earlier checkpoint.
/// </param>
public sealed class UnicodeConversionContext(
    DaemonDbContext db,
    P4ServerInstance instance,
    bool wasRunning,
    string checkpointPath) : InstanceOperationContext(db, instance), ICheckpointContext
{
    public bool WasRunning { get; } = wasRunning;

    public string CheckpointPath { get; } = checkpointPath;

    public P4ServerBuild Build { get; } = instance.Build
        ?? throw new ArgumentException("The instance has no p4d build.", nameof(instance));
}
