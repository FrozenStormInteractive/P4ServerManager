using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Api;

/// <summary>
/// Instance lookups of the CLI, built on the endpoints of <see cref="DaemonClient"/>.
/// </summary>
internal static class DaemonClientInstanceExtensions
{
    /// <summary>
    /// Finds the instance a command names, by its name or its identifier.
    /// </summary>
    public static async Task<InstanceDto> ResolveInstanceAsync(
        this DaemonClient client,
        string reference,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<InstanceDto> instances = await client.GetInstancesAsync(cancellationToken);
        return Resolve(instances, reference);
    }

    /// <summary>
    /// Finds the instances a command names, by their names or their identifiers, in the order given and without
    /// duplicates.
    /// </summary>
    public static async Task<IReadOnlyList<InstanceDto>> ResolveInstancesAsync(
        this DaemonClient client,
        IEnumerable<string> references,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<InstanceDto> instances = await client.GetInstancesAsync(cancellationToken);
        return references.Select(reference => Resolve(instances, reference)).DistinctBy(i => i.Id).ToList();
    }

    private static InstanceDto Resolve(IReadOnlyList<InstanceDto> instances, string reference)
    {
        if (Guid.TryParse(reference, out Guid id) && instances.FirstOrDefault(i => i.Id == id) is { } byId)
        {
            return byId;
        }
        if (instances.FirstOrDefault(i => i.Name == reference) is { } byName)
        {
            return byName;
        }

        // The names are unique as typed only: another case matches when it designates a single instance.
        List<InstanceDto> byOtherCase = instances
            .Where(i => string.Equals(i.Name, reference, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return byOtherCase.Count switch
        {
            1 => byOtherCase[0],
            0 => throw new CliException($"No instance is named {reference}."),
            _ => throw new CliException(
                $"Several instances match {reference}: {string.Join(", ", byOtherCase.Select(i => i.Name))}. "
                + "Type the name with its exact case."),
        };
    }
}
