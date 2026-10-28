using System.CommandLine;

using P4ServerManager.Cli.Api;
using P4ServerManager.Cli.Json;
using P4ServerManager.Cli.Output;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Commands.Builds;

/// <summary>
/// The <c>check</c> and <c>status</c> commands of the archive worker.
/// </summary>
internal static class ArchiveCommands
{
    public static Command CreateCheck(CliServices services)
    {
        Command command = new(
            "check",
            "Check Perforce for new builds of the releases of the archive policy now. The check goes on in the "
            + "daemon.");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            await client.CheckArchiveAsync(cancellationToken);
            if (!invocation.Json)
            {
                await invocation.Output.WriteLineAsync(
                    "Checking Perforce for new builds. Follow it with 'p4sm build status'.");
            }
            return 0;
        });
        return command;
    }

    public static Command CreateStatus(CliServices services)
    {
        Command command = new("status", "Show the archive policy, and what the archive worker does.");

        services.SetAction(command, async (invocation, cancellationToken) =>
        {
            using DaemonClient client = invocation.CreateClient();
            BuildArchiveStatusDto status = await client.GetArchiveStatusAsync(cancellationToken);
            if (invocation.Json)
            {
                invocation.WriteJson(status, CliJsonContext.Default.BuildArchiveStatusDto);
                return 0;
            }

            BuildArchivePolicyDto policy = await client.GetArchivePolicyAsync(cancellationToken);
            string releases = policy.Scope == BuildArchiveScope.AllReleases
                ? "all releases"
                : policy.Releases.Count == 0 ? "none" : string.Join(", ", policy.Releases);
            string activity = status.Activity is null
                ? "idle"
                : status.Percent is { } percent ? $"{status.Activity} ({percent}%)" : status.Activity;

            TextWriter output = invocation.Output;
            await output.WriteLineAsync($"Automatic download: {(policy.AutoDownload ? $"every {policy.CheckIntervalHours} h" : "off")}");
            await output.WriteLineAsync($"Releases:           {releases}");
            await output.WriteLineAsync(
                $"Retention:          {(policy.KeepLatestPerRelease is { } keep ? $"{keep} builds per release" : "keep all")}");
            await output.WriteLineAsync($"Activity:           {activity}");
            foreach (BuildFetchDto fetch in status.Fetches)
            {
                await output.WriteLineAsync($"Fetching:           {BuildFormatting.FormatFetch(fetch)}");
            }
            await output.WriteLineAsync($"Last check:         {BuildFormatting.FormatDate(status.LastCheckAt)}");
            await output.WriteLineAsync($"Next check:         {BuildFormatting.FormatDate(status.NextCheckAt)}");
            if (status.LastCheckError is { } checkError)
            {
                await invocation.Error.WriteLineAsync($"Last check failed: {checkError}");
            }
            if (status.LastDownloadError is { } downloadError)
            {
                await invocation.Error.WriteLineAsync($"Last fetch failed: {downloadError}");
            }
            return 0;
        });
        return command;
    }
}
