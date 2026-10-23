namespace P4ServerManager.Daemon.Database.Entities;

/// <summary>
/// An exact p4d build kept in the build cache, with the matching p4 client.
/// </summary>
public sealed class P4ServerBuild
{
    public Guid Id { get; set; }

    /// <summary>
    /// Release folder it was downloaded from, for example <c>r25.1</c>.
    /// </summary>
    public required string Release { get; set; }

    /// <summary>
    /// Version reported by <c>p4d -V</c>, for example <c>2025.1</c>.
    /// </summary>
    public required string Version { get; set; }

    /// <summary>
    /// Build number reported by <c>p4d -V</c>, for example <c>2850123</c>.
    /// </summary>
    public required string BuildNumber { get; set; }

    /// <summary>
    /// Runtime platform the build runs on, for example <c>linux-x64</c>.
    /// </summary>
    public required string Platform { get; set; }

    /// <summary>
    /// SHA-256 of the p4d executable, lower case.
    /// </summary>
    public required string Sha256 { get; set; }

    public DateTimeOffset DownloadedAt { get; set; }

    public List<P4ServerInstance> Instances { get; set; } = [];
}
