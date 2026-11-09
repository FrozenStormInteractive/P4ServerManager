using System.ComponentModel.DataAnnotations;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// A health check of an instance, kept as a report until it is deleted, or until its instance is.
/// </summary>
public sealed class HealthCheckRun
{
    public Guid Id { get; set; }

    public Guid InstanceId { get; set; }

    public P4ServerInstance? Instance { get; set; }

    [MaxLength(16)]
    public HealthCheckStatus Status { get; set; }

    public List<HealthCheckKind> Checks { get; set; } = [];

    /// <summary>
    /// The p4d build that ran the checks. Text, not a link: the build may be deleted, and the report stays.
    /// </summary>
    [MaxLength(33)]
    public required string P4dVersion { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// Whether the fix journal of <c>p4d -xx</c> is kept in the folder of the instance.
    /// </summary>
    public bool HasFixJournal { get; set; }

    /// <summary>
    /// Whether the check stopped p4d, and p4d was not started again yet. It outlives the daemon, so that a server stopped
    /// for a check that a restart of the daemon cut short does not stay down.
    /// </summary>
    public bool ServerRestartPending { get; set; }

    /// <summary>
    /// Whether the report was deleted while it still tells the health of the instance. It is gone from the list, and is
    /// deleted once a later check replaces it.
    /// </summary>
    public bool ReportHidden { get; set; }

    /// <summary>
    /// The pairs of tables <c>p4d -xx</c> reached, when it ran to its end. p4d tells no number beforehand: the next check
    /// on the same build scales its progress with this one.
    /// </summary>
    public int? CrossCheckPairs { get; set; }

    public List<HealthCheckFinding> Findings { get; set; } = [];
}

/// <summary>
/// A problem a health check found. Its identifier tells the order p4d told them in.
/// </summary>
public sealed class HealthCheckFinding
{
    public int Id { get; set; }

    public Guid RunId { get; set; }

    public HealthCheckRun? Run { get; set; }

    [MaxLength(16)]
    public HealthCheckKind Check { get; set; }

    [MaxLength(128)]
    public string? Table { get; set; }

    public required string Message { get; set; }
}
