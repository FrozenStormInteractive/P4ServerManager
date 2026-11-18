using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Creates the directories of the instance, and the server directory laid out as <see cref="ServerLayout"/> says. The
/// root and the folders next to it must be missing or empty. Only the account the daemon runs as can open the server
/// directory.
/// </summary>
public sealed class PrepareDirectoriesStep(DaemonPaths paths) : IOperationStep<IInstanceOperationContext>
{
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public string Key => "prepare-directories";

    public string Title => "Prepare the directories";

    public Task<string?> RunAsync(
        IInstanceOperationContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        string serverDirectory = ServerLayout.GetServerDirectory(instance.RootPath)
            ?? throw new InstanceOperationException($"The root {instance.RootPath} has no parent folder.");
        string[] serverFolders =
        [
            instance.RootPath,
            .. ServerLayout.SiblingFolders.Select(name => Path.Combine(serverDirectory, name)),
        ];
        foreach (string folder in serverFolders)
        {
            if (File.Exists(folder))
            {
                throw new InstanceOperationException($"{folder} is a file.");
            }
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            {
                throw new InstanceOperationException($"{folder} is not empty.");
            }
        }

        CreatePrivateDirectory(serverDirectory);
        foreach (string folder in serverFolders)
        {
            CreatePrivateDirectory(folder);
        }
        if (instance.Ssl)
        {
            // p4d refuses a key folder that other accounts can open.
            CreatePrivateDirectory(ServerLayout.GetSslDirectory(instance.RootPath));
        }

        Directory.CreateDirectory(paths.GetInstanceDirectory(instance.Id));
        Directory.CreateDirectory(paths.GetRuntimeInstanceDirectory(instance.Id));
        Directory.CreateDirectory(paths.GetClientStateDirectory(instance.Id));
        return Task.FromResult<string?>(serverDirectory);
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateMode);
        }
    }
}
