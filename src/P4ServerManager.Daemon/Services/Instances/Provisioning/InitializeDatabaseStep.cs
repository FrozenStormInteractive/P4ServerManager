using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Creates the p4d database in the root, with the case handling, server ID and Unicode mode of the instance, and points
/// its journal and its log to the folders next to the root.
/// </summary>
public sealed class InitializeDatabaseStep(IP4ServerBuildCache buildCache, IProcessRunner processRunner)
    : IOperationStep<ProvisioningContext>
{
    public string Key => "initialize-database";

    public string Title => "Initialize the database";

    public async Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        string p4dPath = buildCache.GetP4dPath(instance.Build!);
        IReadOnlyList<string> root = P4ServerLauncher.GetRootArguments(instance);
        TimeSpan timeout = ProcessRunnerExtensions.CommandTimeout;

        // -xu creates the database, with the case handling given by -C1; -xD sets the server ID.
        await processRunner.RunCheckedAsync(
            new ProcessCommand(p4dPath, [.. root, "-xu"], Timeout: timeout),
            cancellationToken);
        await processRunner.RunCheckedAsync(
            new ProcessCommand(p4dPath, [.. root, "-xD", instance.Name], Timeout: timeout),
            cancellationToken);

        // Relative to the root. One argument each: p4d reads "-c" followed by the command.
        string[] settings =
        [
            $"P4JOURNAL=../{ServerLayout.JournalsFolder}/journal",
            $"P4LOG=../{ServerLayout.LogsFolder}/{ServerLayout.LogFile}",
        ];
        foreach (string setting in settings)
        {
            await processRunner.RunCheckedAsync(
                new ProcessCommand(p4dPath, [.. root, $"-cset {setting}"], Timeout: timeout),
                cancellationToken);
        }
        if (instance.Unicode)
        {
            await processRunner.RunCheckedAsync(
                new ProcessCommand(p4dPath, [.. root, "-xi"], Timeout: timeout),
                cancellationToken);
        }
        return null;
    }
}
