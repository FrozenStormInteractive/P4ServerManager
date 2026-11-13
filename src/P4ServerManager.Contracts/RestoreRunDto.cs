namespace P4ServerManager.Contracts;

public enum RestoreTarget
{
    /// <summary>
    /// The point replaced the data of an instance. Its previous data was kept aside until the restore succeeded.
    /// </summary>
    ExistingInstance,

    /// <summary>
    /// The point became a new instance.
    /// </summary>
    NewInstance,
}

public enum RestoreRunStatus
{
    Running,

    /// <summary>
    /// The server runs on the restored data, and the verification of its archives found nothing wrong.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The server runs on the restored data, but the verification of its archives found problems, or could not run.
    /// </summary>
    SucceededWithProblems,

    /// <summary>
    /// The restore failed. An instance whose data it replaced got its previous data back.
    /// </summary>
    Failed,

    /// <summary>
    /// A user stopped the restore. An instance whose data it replaced got its previous data back.
    /// </summary>
    Canceled,

    /// <summary>
    /// The daemon stopped during the restore. An instance whose data it replaced got its previous data back when the
    /// daemon started again.
    /// </summary>
    Interrupted,
}

/// <summary>
/// A restore of a backup point, running or ended. It is kept after the instance is deleted.
/// </summary>
/// <param name="Id">Identifier of the restore.</param>
/// <param name="InstanceId">The instance restored to.</param>
/// <param name="InstanceName">Its name at the time of the restore.</param>
/// <param name="Target">Whether the point replaced the data of an instance or became a new one.</param>
/// <param name="DestinationId">The destination the point was read from. It may have been deleted since.</param>
/// <param name="DestinationName">Its name at the time of the restore.</param>
/// <param name="SnapshotKey">Key of the manifest of the point.</param>
/// <param name="PointId">Identifier of the point, the one of the backup that made it.</param>
/// <param name="PointCreatedAt">When the backup of the point began.</param>
/// <param name="SourceName">Name of the instance that was backed up.</param>
/// <param name="Status">Whether it runs, or how it ended.</param>
/// <param name="StartedAt">When it began.</param>
/// <param name="FinishedAt">When it ended, or null while it runs.</param>
/// <param name="FileCount">Files of the point.</param>
/// <param name="TotalBytes">Their size.</param>
/// <param name="DownloadedBytes">Bytes downloaded from the destination.</param>
/// <param name="ReusedFiles">Files the instance already had, unchanged, put back rather than downloaded.</param>
/// <param name="VerifyProblemCount">Problems <c>p4 verify</c> found in the archives.</param>
/// <param name="VerifyProblems">The first of them.</param>
/// <param name="Warnings">What the restore could not do, though it succeeded.</param>
/// <param name="Error">Why it failed.</param>
public sealed record RestoreRunDto(
    Guid Id,
    Guid InstanceId,
    string InstanceName,
    RestoreTarget Target,
    Guid DestinationId,
    string DestinationName,
    string SnapshotKey,
    Guid PointId,
    DateTimeOffset? PointCreatedAt,
    string? SourceName,
    RestoreRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int FileCount,
    long TotalBytes,
    long DownloadedBytes,
    int ReusedFiles,
    int VerifyProblemCount,
    IReadOnlyList<string> VerifyProblems,
    IReadOnlyList<string> Warnings,
    string? Error);
