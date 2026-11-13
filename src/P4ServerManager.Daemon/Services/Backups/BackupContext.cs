using P4ServerManager.Backup.Manifests;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Backups.Destinations;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups;

/// <summary>
/// Labels of the manifests the daemon writes.
/// </summary>
public static class BackupLabels
{
    public const string CheckpointNumber = "checkpoint";

    /// <summary>
    /// The last journal of the point, absent from a full backup.
    /// </summary>
    public const string JournalNumber = "journal";

    public const string ServerId = "serverId";

    public const string P4dVersion = "p4dVersion";

    public const string Unicode = "unicode";

    public const string CaseSensitive = "caseSensitive";

    /// <summary>
    /// Start of the label of each depot whose archives an object store keeps, and this backup lacks, followed by its
    /// name. Its value is the configuration of the store, from <c>db.depot</c>, to find the archives on restore.
    /// </summary>
    public const string ObjectStoreDepotPrefix = "objectStoreDepot:";
}

/// <summary>
/// Where a backup is at one of its destinations.
/// </summary>
/// <param name="destination">The destination.</param>
/// <param name="record">What the run keeps of it.</param>
public sealed class BackupDestinationState(BackupDestination destination, BackupRunDestination record)
{
    public BackupDestination Destination { get; } = destination;

    public BackupRunDestination Record { get; } = record;

    public OpenedBackupDestination? Opened { get; set; }

    /// <summary>
    /// The latest point of the instance at the destination: the base of an incremental backup.
    /// </summary>
    public BackupManifest? Base { get; set; }

    /// <summary>
    /// Objects the destination has for sure: the ones of <see cref="Base"/>.
    /// </summary>
    public HashSet<string> KnownObjects { get; } = new(StringComparer.Ordinal);

    public bool Failed => Record.Status == BackupDestinationStatus.Failed;

    public void Fail(string error)
    {
        Record.Status = BackupDestinationStatus.Failed;
        Record.Error = error;
    }
}

/// <summary>
/// State shared by the steps of a backup of an instance. p4d keeps running.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/> and <paramref name="run"/>.</param>
/// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
/// <param name="run">The run, saved as running, with a record of each destination.</param>
/// <param name="destinations">The destinations, in the order of the records of the run.</param>
public sealed class BackupContext(
    DaemonDbContext db,
    P4ServerInstance instance,
    BackupRun run,
    IReadOnlyList<BackupDestination> destinations) : InstanceOperationContext(db, instance), IOperationOutcome
{
    public const string CompletedOutcome = "completed";

    public const string PartiallyCompletedOutcome = "partially-completed";

    public BackupRun Run { get; } = run;

    public BackupKind Kind => Run.Kind;

    public IReadOnlyList<BackupDestinationState> Destinations { get; } = destinations
        .Select(d => new BackupDestinationState(d, run.Destinations.Single(r => r.DestinationId == d.Id)))
        .ToList();

    /// <summary>
    /// Server ID of p4d, read before the backup.
    /// </summary>
    public string? ServerId { get; init; }

    public ServerFileLayout? Layout { get; set; }

    /// <summary>
    /// What the files were at the last backup.
    /// </summary>
    public BackupFileIndex? Index { get; set; }

    /// <summary>
    /// The checkpoint, its MD5 or the journal this backup made.
    /// </summary>
    public List<BackupFile> MetadataFiles { get; } = [];

    /// <summary>
    /// The archives and the files of the root.
    /// </summary>
    public List<BackupFile> ServerFiles { get; } = [];

    public string Outcome => Destinations.All(d => d.Record.Status == BackupDestinationStatus.Succeeded)
        ? CompletedOutcome
        : PartiallyCompletedOutcome;

    /// <summary>
    /// Releases the repositories of the destinations.
    /// </summary>
    public async Task CloseDestinationsAsync()
    {
        foreach (BackupDestinationState destination in Destinations)
        {
            if (destination.Opened is not null)
            {
                await destination.Opened.DisposeAsync();
                destination.Opened = null;
            }
        }
    }
}
