using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Output;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Builds;

internal static class ListBuildsCommand
{
    public static Command Create(CliServices services)
    {
        Command command = new("list", "List the builds of the archive.");
        command.Aliases.Add("ls");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            IReadOnlyList<BuildDto> builds = await client.GetBuildsAsync(cancellationToken);

            if (invocation.Json)
            {
                invocation.WriteJson(builds, CliJsonContext.Default.IReadOnlyListBuildDto);
                return 0;
            }
            if (builds.Count == 0)
            {
                await invocation.Output.WriteLineAsync(
                    "The archive is empty. Download a build with 'p4sm build download' or import one with "
                    + "'p4sm build import'.");
                return 0;
            }

            TableWriter table = new("RELEASE", "VERSION", "SOURCE", "ARCHIVE", "EXTRACTED", "PROTECTED", "USED BY");
            foreach (BuildDto build in builds)
            {
                table.AddRow(
                    build.Release,
                    BuildFormatting.FormatVersion(build),
                    build.Source.ToString(),
                    BuildFormatting.FormatSize(build.ArchiveSize),
                    InstanceFormatting.FormatYesNo(build.Extracted),
                    InstanceFormatting.FormatYesNo(build.Protected),
                    BuildFormatting.FormatInstances(build));
            }
            table.Write(invocation.Output);
            return 0;
        });
        return command;
    }
}
