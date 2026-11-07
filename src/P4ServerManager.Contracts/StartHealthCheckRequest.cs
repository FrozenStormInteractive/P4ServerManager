using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Contracts;

/// <summary>
/// The checks a health check runs.
/// </summary>
public sealed record StartHealthCheckRequest
{
    /// <summary>
    /// The checks, at least one. They always run in the order of <see cref="HealthCheckKind"/>.
    /// </summary>
    [Required]
    public IReadOnlyList<HealthCheckKind> Checks { get; init; } = null!;
}
