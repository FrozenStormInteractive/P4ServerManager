using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.UnicodeConversion;

/// <summary>
/// Switches the database to Unicode mode with <c>p4d -xi</c>, which first checks that its metadata is valid UTF-8, and
/// names the tables that are not. The mode is then read back from the database: it decides how the daemon talks to p4d
/// from now on, so it does not rest on the exit code alone.
/// </summary>
public sealed class SwitchToUnicodeStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    InstanceNotifier notifier) : IOperationStep<UnicodeConversionContext>
{
    public string Key => "switch-to-unicode";

    public string Title => "Switch to Unicode mode";

    public async Task<string?> RunAsync(
        UnicodeConversionContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        string p4dPath = buildCache.GetP4dPath(context.Build);
        IReadOnlyList<string> root = P4ServerLauncher.GetRootArguments(instance);

        // Not canceled once begun: a p4d killed while it writes to the database can leave it unusable.
        ProcessResult result = await processRunner.RunAsync(
            new ProcessCommand(p4dPath, [.. root, "-xi"], Timeout: SaveCheckpointStep.Timeout),
            CancellationToken.None);
        if (!result.Succeeded)
        {
            throw new InstanceOperationException($"'p4d -xi' failed: {DescribeFailure(result)}");
        }

        ProcessResult dump = await processRunner.RunCheckedAsync(
            new ProcessCommand(
                p4dPath,
                [.. root, "-jd", "-", "db.config"],
                Timeout: ProcessRunnerExtensions.CommandTimeout),
            CancellationToken.None);
        if (!ConfigDumpParser.IsUnicode(dump.StandardOutput))
        {
            throw new InstanceOperationException("p4d did not switch the server to Unicode mode.");
        }

        instance.Unicode = true;
        await context.Db.SaveChangesAsync(CancellationToken.None);
        await notifier.InstanceChangedAsync(instance);
        return "Unicode mode";
    }

    /// <summary>
    /// Both outputs on one line: p4d names the tables with invalid UTF-8 on its standard output, and only counts them on
    /// its error output.
    /// </summary>
    private static string DescribeFailure(ProcessResult result)
    {
        IEnumerable<string> lines = $"{result.StandardOutput}\n{result.StandardError}"
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line != "Perforce server error:");
        return string.Join(' ', lines);
    }
}
