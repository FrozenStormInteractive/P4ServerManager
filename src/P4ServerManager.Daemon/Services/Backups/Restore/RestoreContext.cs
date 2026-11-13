using P4ServerManager.Backup.Manifests;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Backups.Destinations;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Restore;

/// <summary>
/// State shared by the steps of a restore.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/> and <paramref name="run"/>.</param>
/// <param name="instance">The instance restored to, with its <see cref="P4ServerInstance.Build"/> loaded once known.</param>
/// <param name="run">The restore, saved as running.</param>
/// <param name="destination">The destination that holds the point.</param>
public class RestoreContext(
    DaemonDbContext db,
    P4ServerInstance instance,
    RestoreRun run,
    BackupDestination destination) : InstanceOperationContext(db, instance), IOperationOutcome
{
    public const string RestoredOutcome = "restored";

    public const string RestoredWithProblemsOutcome = "restored-with-problems";

    public RestoreRun Run { get; } = run;

    public BackupDestination Destination { get; } = destination;

    /// <summary>
    /// Whether the point replaces the data of the instance, rather than making a new one.
    /// </summary>
    public bool ReplacesData => Run.Target == RestoreTarget.ExistingInstance;

    /// <summary>
    /// Whether p4d ran before the restore: it runs again after it, whatever the outcome.
    /// </summary>
    public bool WasRunning { get; init; }

    /// <summary>
    /// The super user that verifies the archives, and relocates depots for a new instance. Null when none is usable:
    /// the verification is then skipped.
    /// </summary>
    public SuperUserCredentials? SuperUser { get; set; }

    /// <summary>
    /// Whether to restore the license file of the point.
    /// </summary>
    public bool RestoreLicense { get; init; } = true;

    /// <summary>
    /// The destination, once opened.
    /// </summary>
    public OpenedBackupDestination? Opened { get; set; }

    /// <summary>
    /// The point, once read.
    /// </summary>
    public BackupManifest? Manifest { get; set; }

    /// <summary>
    /// The server ID of the server that was backed up, whose configurables apply to the restored database, or null when
    /// it had none.
    /// </summary>
    public string? PointServerId { get; set; }

    /// <summary>
    /// The folders of the instance moved aside, when the point replaces its data.
    /// </summary>
    public RestoreSetAside? SetAside { get; set; }

    /// <summary>
    /// Folder where the checkpoint and the journals wait for the replay.
    /// </summary>
    public string StagingDirectory => Path.Combine(Instance.RootPath, ".p4sm-restore");

    /// <summary>
    /// What the restored database said of the layout before any change: what each path of the point is.
    /// </summary>
    public ServerDescription? OriginalDescription { get; set; }

    /// <summary>
    /// Where each file goes, once the database is restored.
    /// </summary>
    public RestorePlan? Plan { get; set; }

    /// <summary>
    /// Whether the restore started p4d.
    /// </summary>
    public bool ServerStarted { get; set; }

    /// <summary>
    /// Stops the verification of the archives only: the restore then ends with a warning.
    /// </summary>
    public CancellationTokenSource VerifyCancellation { get; } = new();

    /// <summary>
    /// Whether the archives are being verified: a cancellation then stops only the verification.
    /// </summary>
    public bool Verifying { get; set; }

    public string Outcome => Run.VerifyProblemCount > 0 || Run.Warnings.Count > 0
        ? RestoredWithProblemsOutcome
        : RestoredOutcome;

    public BackupManifest RequireManifest()
    {
        return Manifest ?? throw new InvalidOperationException("The point is not read yet.");
    }

    public OpenedBackupDestination RequireOpened()
    {
        return Opened ?? throw new InvalidOperationException("The destination is not open yet.");
    }

    public void AddWarning(string warning)
    {
        if (!Run.Warnings.Contains(warning))
        {
            Run.Warnings.Add(warning);
        }
    }

    public async ValueTask CloseAsync()
    {
        if (Opened is not null)
        {
            await Opened.DisposeAsync();
            Opened = null;
        }
        VerifyCancellation.Dispose();
    }
}
