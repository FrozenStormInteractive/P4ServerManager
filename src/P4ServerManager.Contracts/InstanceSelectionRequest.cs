namespace P4ServerManager.Contracts;

/// <summary>
/// The instances a start, a stop or a restart applies to: the ones listed, or all of them.
/// </summary>
public sealed record InstanceSelectionRequest
{
    /// <summary>
    /// Identifiers of the instances. Left out when <see cref="All"/> is true.
    /// </summary>
    public IReadOnlyList<Guid>? InstanceIds { get; init; }

    /// <summary>
    /// True for every ready instance, instead of a list.
    /// </summary>
    public bool All { get; init; }
}
