using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Moves the current data of the instance aside: its root, the folder of its journals and the folders of its archives,
/// each renamed next to itself. They come back if the restore fails, and are deleted once it succeeds. An empty root
/// takes the place of the previous one.
/// </summary>
public sealed class SetAsideServerFilesStep(
    ServerFileLocator locator,
    DaemonPaths paths,
    ILogger<SetAsideServerFilesStep> logger) : IOperationStep<RestoreContext>
{
    public string Key => "set-aside-server-files";

    public string Title => "Set the current data aside";

    public bool IsIncluded(RestoreContext context)
    {
        return context.ReplacesData;
    }

    public async Task<string?> RunAsync(
        RestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        string rootPath = Path.GetFullPath(context.Instance.RootPath);
        List<string> folders = [rootPath];
        try
        {
            ServerFileLayout layout = await locator.ReadLayoutAsync(context.Instance, cancellationToken);
            folders.Add(layout.JournalDirectory);
            folders.AddRange(layout.ArchiveSources.Select(s => s.Directory));
        }
        catch (InstanceOperationException exception)
        {
            // A damaged server is a reason to restore: its usual folders are set aside instead.
            logger.LogWarning(
                exception,
                "The layout of instance {Name} cannot be read: its usual folders are set aside",
                context.Instance.Name);
            if (ServerLayout.GetServerDirectory(rootPath) is { } serverDirectory)
            {
                folders.AddRange(ServerLayout.SiblingFolders
                    .Where(name => name != ServerLayout.LogsFolder)
                    .Select(name => Path.Combine(serverDirectory, name)));
            }
        }

        string[] protectedFolders =
        [
            paths.GetInstanceDirectory(context.Instance.Id),
            paths.GetRuntimeInstanceDirectory(context.Instance.Id),
            paths.DataDirectory,
            paths.CacheDirectory,
            paths.RuntimeDirectory,
        ];
        foreach (string folder in folders)
        {
            if (protectedFolders.FirstOrDefault(p => ServerFileLocator.Contains(folder, p)) is { } held)
            {
                throw new InstanceOperationException(
                    $"{folder} holds {held}, where the daemon keeps its own files: it cannot be set aside.");
            }
        }

        RestoreSetAside setAside = await RestoreSetAside.BeginAsync(
            paths.GetRestoreStatePath(context.Instance.Id),
            context.Run.Id,
            context.WasRunning,
            cancellationToken);
        context.SetAside = setAside;

        // A folder inside another one goes with it.
        foreach (string folder in folders.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(f => f.Length))
        {
            progress.Report(new OperationStepProgress(folder, null));
            await setAside.MoveAsideAsync(folder, cancellationToken);
        }

        // The same folders, empty: p4d opens the journal of the restored configuration as soon as it reads the database.
        foreach (SetAsideFolder folder in setAside.Folders)
        {
            RestoreDirectories.CreatePrivate(folder.Original);
        }
        int moved = setAside.Folders.Count(f => f.Aside is not null);
        return $"{moved} folders set aside";
    }
}

/// <summary>
/// Creates the folders of a restored server.
/// </summary>
public static class RestoreDirectories
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates a folder only the account of the daemon can open: p4d refuses a key folder that others can.
    /// </summary>
    public static void CreatePrivate(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
    }

    /// <summary>
    /// Makes a file readable by the account of the daemon only, such as the private key of the server.
    /// </summary>
    public static void MakePrivate(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, PrivateFileMode);
        }
    }
}
