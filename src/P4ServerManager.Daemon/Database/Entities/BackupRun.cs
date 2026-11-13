using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// A backup of an instance. Not linked to the instance: the history outlives it, as its points stay at the
/// destinations. Its identifier is the one of its backup point.
/// </summary>
[Index(nameof(InstanceId))]
public sealed class BackupRun
{
    public Guid Id { get; set; }

    public Guid InstanceId { get; set; }

    [MaxLength(32)]
    public required string InstanceName { get; set; }

    [MaxLength(16)]
    public BackupKind Kind { get; set; }

    [MaxLength(16)]
    public BackupTrigger Trigger { get; set; }

    [MaxLength(32)]
    public BackupRunStatus Status { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int? CheckpointNumber { get; set; }

    public int? JournalNumber { get; set; }

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public string? Error { get; set; }

    public List<BackupRunDestination> Destinations { get; set; } = [];
}

/// <summary>
/// What a backup did at one of its destinations, and the last verification of its point there. Not linked to the
/// destination, which may be deleted: its name is kept.
/// </summary>
public sealed class BackupRunDestination
{
    public int Id { get; set; }

    public Guid RunId { get; set; }

    public BackupRun? Run { get; set; }

    public Guid DestinationId { get; set; }

    [MaxLength(64)]
    public required string DestinationName { get; set; }

    [MaxLength(16)]
    public BackupDestinationStatus Status { get; set; }

    public int UploadedObjects { get; set; }

    public long UploadedBytes { get; set; }

    [MaxLength(128)]
    public string? SnapshotKey { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public bool VerificationDeep { get; set; }

    public bool VerificationSucceeded { get; set; }

    public int VerificationCheckedObjects { get; set; }

    public int VerificationProblemCount { get; set; }

    /// <summary>
    /// The first problems the last verification found.
    /// </summary>
    public List<string> VerificationProblems { get; set; } = [];

    /// <summary>
    /// Why the last verification could not run.
    /// </summary>
    public string? VerificationError { get; set; }
}
