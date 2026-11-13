using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// A restore of a backup point to an instance. Not linked to the instance or to the destination: the history outlives
/// them.
/// </summary>
[Index(nameof(InstanceId))]
public sealed class RestoreRun
{
    public Guid Id { get; set; }

    public Guid InstanceId { get; set; }

    [MaxLength(32)]
    public required string InstanceName { get; set; }

    [MaxLength(32)]
    public RestoreTarget Target { get; set; }

    public Guid DestinationId { get; set; }

    [MaxLength(64)]
    public required string DestinationName { get; set; }

    [MaxLength(128)]
    public required string SnapshotKey { get; set; }

    public Guid PointId { get; set; }

    public DateTimeOffset? PointCreatedAt { get; set; }

    [MaxLength(32)]
    public string? SourceName { get; set; }

    [MaxLength(32)]
    public RestoreRunStatus Status { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public long DownloadedBytes { get; set; }

    public int ReusedFiles { get; set; }

    public int VerifyProblemCount { get; set; }

    /// <summary>
    /// The first problems <c>p4 verify</c> found.
    /// </summary>
    public List<string> VerifyProblems { get; set; } = [];

    /// <summary>
    /// What the restore could not do, though it succeeded.
    /// </summary>
    public List<string> Warnings { get; set; } = [];

    public string? Error { get; set; }
}
