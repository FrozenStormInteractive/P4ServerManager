namespace P4ServerManager.Contracts;

public enum BackupKind
{
    /// <summary>
    /// A checkpoint of the database, with every archive: restorable on its own.
    /// </summary>
    Full,

    /// <summary>
    /// A rotation of the journal, with the new archives: restorable with the backups it builds on, from the latest full
    /// one.
    /// </summary>
    Incremental,
}

public enum BackupTrigger
{
    /// <summary>
    /// Asked for by a user.
    /// </summary>
    Manual,

    /// <summary>
    /// Begun by a schedule.
    /// </summary>
    Scheduled,
}

public enum BackupRunStatus
{
    Running,

    /// <summary>
    /// Every destination has the backup.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Some destinations have the backup, others failed.
    /// </summary>
    PartiallySucceeded,

    /// <summary>
    /// No destination has the backup.
    /// </summary>
    Failed,

    /// <summary>
    /// Canceled on request. No destination has the backup.
    /// </summary>
    Canceled,

    /// <summary>
    /// Cut short by the end of the daemon.
    /// </summary>
    Interrupted,
}

public enum BackupDestinationStatus
{
    Pending,
    Uploading,

    /// <summary>
    /// The backup point is written at the destination.
    /// </summary>
    Succeeded,

    Failed,
}

/// <summary>
/// The last verification of a backup point at a destination.
/// </summary>
/// <param name="VerifiedAt">When it ended.</param>
/// <param name="Deep">Whether every object was read back, rather than only found.</param>
/// <param name="Succeeded">Whether nothing was found wrong.</param>
/// <param name="CheckedObjects">Objects checked.</param>
/// <param name="ProblemCount">Problems found.</param>
/// <param name="Problems">The first problems found.</param>
/// <param name="Error">Why the verification could not run, such as an unreachable storage.</param>
public sealed record BackupVerificationDto(
    DateTimeOffset VerifiedAt,
    bool Deep,
    bool Succeeded,
    int CheckedObjects,
    int ProblemCount,
    IReadOnlyList<string> Problems,
    string? Error);

/// <summary>
/// What a backup did at one of its destinations.
/// </summary>
/// <param name="DestinationId">The destination. It may have been deleted since.</param>
/// <param name="DestinationName">Its name at the time of the backup.</param>
/// <param name="Status">Where the upload is, or how it ended.</param>
/// <param name="UploadedObjects">Objects the destination did not have yet, uploaded.</param>
/// <param name="UploadedBytes">Bytes uploaded, compressed and encrypted.</param>
/// <param name="SnapshotKey">Key of the manifest of the backup point, once written.</param>
/// <param name="Error">Why the destination failed.</param>
/// <param name="LastVerification">The last verification of the point at this destination.</param>
public sealed record BackupRunDestinationDto(
    Guid DestinationId,
    string DestinationName,
    BackupDestinationStatus Status,
    int UploadedObjects,
    long UploadedBytes,
    string? SnapshotKey,
    string? Error,
    BackupVerificationDto? LastVerification);

/// <summary>
/// A backup of an instance, running or ended. It is kept after the instance is deleted: its points stay at the
/// destinations.
/// </summary>
/// <param name="Id">Identifier of the backup, and of its backup point.</param>
/// <param name="InstanceId">The instance.</param>
/// <param name="InstanceName">Its name at the time of the backup.</param>
/// <param name="Kind">Full or incremental.</param>
/// <param name="Trigger">What began it.</param>
/// <param name="Status">Whether it runs, or how it ended.</param>
/// <param name="StartedAt">When it began.</param>
/// <param name="FinishedAt">When it ended, or null while it runs.</param>
/// <param name="CheckpointNumber">The checkpoint the point builds on.</param>
/// <param name="JournalNumber">The last journal of the point, for an incremental backup.</param>
/// <param name="FileCount">Files of the point: checkpoint, journals, archives and files of the root.</param>
/// <param name="TotalBytes">Their size.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="Destinations">What it did at each destination.</param>
public sealed record BackupRunDto(
    Guid Id,
    Guid InstanceId,
    string InstanceName,
    BackupKind Kind,
    BackupTrigger Trigger,
    BackupRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int? CheckpointNumber,
    int? JournalNumber,
    int FileCount,
    long TotalBytes,
    string? Error,
    IReadOnlyList<BackupRunDestinationDto> Destinations);
