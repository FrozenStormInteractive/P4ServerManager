using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Creates the directories of the instance. The root must be missing or empty.
/// </summary>
public sealed class PrepareDirectoriesStep(DaemonPaths paths) : IOperationStep<ProvisioningContext>
{
    public string Key => "prepare-directories";

    public string Title => "Prepare the directories";

    public Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        Directory.CreateDirectory(paths.GetInstanceDirectory(instance.Id));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.GetLogPath(instance.Id))!);
        Directory.CreateDirectory(paths.GetClientStateDirectory(instance.Id));

        if (File.Exists(instance.RootPath))
        {
            throw new InstanceOperationException($"The root {instance.RootPath} is a file.");
        }
        if (Directory.Exists(instance.RootPath) && Directory.EnumerateFileSystemEntries(instance.RootPath).Any())
        {
            throw new InstanceOperationException($"The root {instance.RootPath} is not empty.");
        }
        Directory.CreateDirectory(instance.RootPath);
        return Task.FromResult<string?>(instance.RootPath);
    }
}
