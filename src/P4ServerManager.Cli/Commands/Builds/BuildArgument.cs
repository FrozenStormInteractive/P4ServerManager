using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Builds;

internal static class BuildArgument
{
    public static Argument<string> Create()
    {
        return new Argument<string>("build")
        {
            Description = "Build number, such as 2850123, version and build, such as 2025.1/2850123, or identifier.",
        };
    }

    /// <summary>
    /// Finds the build a command names, by its number, its version and number, or its identifier.
    /// </summary>
    public static async Task<BuildDto> ResolveAsync(
        DaemonClient client,
        string reference,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BuildDto> builds = await client.GetBuildsAsync(cancellationToken);
        if (Guid.TryParse(reference, out Guid id) && builds.FirstOrDefault(b => b.Id == id) is { } byId)
        {
            return byId;
        }

        List<BuildDto> matches = builds
            .Where(b => b.Build == reference || $"{b.Version}/{b.Build}" == reference)
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No build of the archive matches {reference}. List them with 'p4sm build list'."),
            _ => throw new CliException(
                $"Several builds match {reference}: "
                + $"{string.Join(", ", matches.Select(b => $"{b.Version}/{b.Build}"))}. Give the version too."),
        };
    }
}
