using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Builds;

internal static class DownloadBuildCommand
{
    public static Command Create(CliServices services)
    {
        Argument<string> releaseArgument = new("release")
        {
            Description = "p4d release, for example r25.1. Perforce only offers its latest patch.",
        };
        Command command = new(
            "download",
            "Download the current build of a release from Perforce into the archive. The download goes on in "
            + "the daemon.")
        {
            releaseArgument,
        };

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            string release = invocation.GetValue(releaseArgument);
            using DaemonClient client = invocation.CreateClient();
            await client.DownloadBuildAsync(new DownloadBuildRequest { Release = release }, cancellationToken);
            if (!invocation.Json)
            {
                await invocation.Output.WriteLineAsync(
                    $"Downloading the current build of {release}. Follow it with 'p4sm build status'.");
            }
            return 0;
        });
        return command;
    }
}
