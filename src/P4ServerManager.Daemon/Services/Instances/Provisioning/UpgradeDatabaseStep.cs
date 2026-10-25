using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Upgrades the database of an imported server to the build of the instance. A database already at that level is
/// left as it is; p4d refuses a database newer than the build.
/// </summary>
public sealed class UpgradeDatabaseStep(IP4ServerBuildCache buildCache, IProcessRunner processRunner)
    : IOperationStep<ProvisioningContext>
{
    public string Key => "upgrade-database";

    public string Title => "Upgrade the database";

    public async Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(instance.Build!),
                [.. P4ServerLauncher.GetRootArguments(instance), "-xu"],
                Timeout: SaveCheckpointStep.Timeout),
            cancellationToken);
        return $"p4d {instance.Build!.Version}";
    }
}
