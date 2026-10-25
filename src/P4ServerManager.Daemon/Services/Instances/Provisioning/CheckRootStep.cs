using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Import;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Creates the directories of an imported instance, and checks that its root holds a database the daemon can open.
/// The root itself is left as it is.
/// </summary>
public sealed class CheckRootStep(DaemonPaths paths) : IOperationStep<ProvisioningContext>
{
    public string Key => "check-root";

    public string Title => "Check the root";

    public Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        if (LegacyProfileStore.CheckRoot(instance.RootPath) is { } problem)
        {
            throw new InstanceOperationException(problem);
        }

        Directory.CreateDirectory(paths.GetInstanceDirectory(instance.Id));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.GetLogPath(instance.Id))!);
        Directory.CreateDirectory(paths.GetClientStateDirectory(instance.Id));
        return Task.FromResult<string?>(instance.RootPath);
    }
}
