namespace P4ServerManager.Contracts;

/// <summary>
/// A check of the database of an instance.
/// </summary>
public enum HealthCheckKind
{
    /// <summary>
    /// Low-level validation of the database tables, with <c>p4d -xv</c>.
    /// </summary>
    Validate,

    /// <summary>
    /// Consistency between pairs of tables, with <c>p4d -xx</c>.
    /// </summary>
    CrossCheck,
}

public enum HealthCheckStatus
{
    Running,

    /// <summary>
    /// Every check ran to its end and found nothing.
    /// </summary>
    Healthy,

    /// <summary>
    /// At least one check found problems in the database.
    /// </summary>
    ProblemsFound,

    /// <summary>
    /// A check could not run, which says nothing of the database. <see cref="HealthCheckRunDto.Error"/> tells why.
    /// </summary>
    Failed,

    /// <summary>
    /// Canceled on request. What was found until then is kept.
    /// </summary>
    Canceled,

    /// <summary>
    /// Cut short by the end of the daemon. What was found until then is kept.
    /// </summary>
    Interrupted,
}

/// <summary>
/// A problem a check found.
/// </summary>
/// <param name="Check">The check that found it.</param>
/// <param name="Table">The table, or the pair of tables such as <c>db.change/db.desc</c>, when p4d names it.</param>
/// <param name="Message">The problem, as p4d tells it.</param>
public sealed record HealthCheckFindingDto(HealthCheckKind Check, string? Table, string Message);

/// <summary>
/// How far a running health check is.
/// </summary>
/// <param name="Check">The check that runs.</param>
/// <param name="Item">The table or the pair of tables it is on, once it has begun.</param>
/// <param name="ItemsChecked">Tables or pairs of tables this check has reached.</param>
/// <param name="Percent">
/// Progress of the whole run, from 0 to 100. It counts tables, not time, from an estimate of their number: it may stay
/// long on a large table and jump at the end.
/// </param>
public sealed record HealthCheckProgressDto(HealthCheckKind Check, string? Item, int ItemsChecked, int Percent);

/// <summary>
/// A health check of an instance, running or kept as a report. A report stays until it is deleted.
/// </summary>
/// <param name="Id">Identifier of the run.</param>
/// <param name="InstanceId">The instance.</param>
/// <param name="Status">Whether it runs, or how it ended.</param>
/// <param name="Checks">The checks asked for, in the order they run.</param>
/// <param name="P4dVersion">The p4d build that ran the checks, such as <c>2026.1/3089746</c>.</param>
/// <param name="StartedAt">When it began.</param>
/// <param name="FinishedAt">When it ended, or null while it runs.</param>
/// <param name="Error">Why a check could not run to its end.</param>
/// <param name="HasFixJournal">
/// Whether the fix journal <c>p4d -xx</c> wrote is kept. Perforce Support reads it to repair the tables.
/// </param>
/// <param name="Progress">How far it is, while it runs.</param>
/// <param name="Findings">The problems found so far, in the order p4d told them.</param>
/// <param name="ReportHidden">
/// Whether the report was deleted while it still tells the health of the instance. Clients leave it out of the list of
/// reports. The daemon deletes it once a later check replaces it.
/// </param>
public sealed record HealthCheckRunDto(
    Guid Id,
    Guid InstanceId,
    HealthCheckStatus Status,
    IReadOnlyList<HealthCheckKind> Checks,
    string P4dVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    bool HasFixJournal,
    HealthCheckProgressDto? Progress,
    IReadOnlyList<HealthCheckFindingDto> Findings,
    bool ReportHidden = false);
