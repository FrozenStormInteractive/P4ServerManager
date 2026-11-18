using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Deletion;

/// <summary>
/// Tells which files the deletion of an instance with its files removes, whatever created its server directory: the
/// daemon, the user at creation, or an import. Only what the server uses goes, read from its own configuration: its
/// root, the folders of its depots, its checkpoints and journals, its logs. Their folders go too once nothing else is
/// left in them, and so does the server directory. A file the daemon does not know keeps its folder. The folders the
/// daemon keeps for the instance are not part of the plan: they go with every deletion, files or not.
/// </summary>
public sealed class InstanceFilePlanner(
    ServerFileLocator locator,
    P4ServerLauncher launcher,
    DaemonPaths paths,
    ILogger<InstanceFilePlanner> logger)
{
    // Every this many entries, a deletion checks whether the daemon stops.
    private const int CancellationCheckInterval = 256;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Reads the configuration of the server, then plans the deletion. When it cannot be read, as for a server never
    /// provisioned or damaged, the root and the usual folders next to it are planned instead.
    /// </summary>
    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    /// <param name="otherRootPaths">The roots of the other instances, which the deletion must not reach.</param>
    /// <param name="cancellationToken">Aborts the read of the configuration.</param>
    public async Task<InstanceFilePlan> PlanAsync(
        P4ServerInstance instance,
        IReadOnlyList<string> otherRootPaths,
        CancellationToken cancellationToken)
    {
        ServerDescription? description = null;
        if (instance.Build is not null)
        {
            try
            {
                description = await locator.ReadDescriptionAsync(instance, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "The configuration of instance {Name} cannot be read: the usual folders of its server are planned for deletion",
                    instance.Name);
            }
        }

        return Plan(
            instance.RootPath,
            description,
            launcher.GetLogPath(instance),
            [paths.GetInstanceDirectory(instance.Id), paths.GetRuntimeInstanceDirectory(instance.Id)],
            otherRootPaths,
            [paths.DataDirectory, paths.CacheDirectory, paths.RuntimeDirectory, paths.ArchiveDirectory, paths.ServersDirectory]);
    }

    /// <summary>
    /// Plans the deletion from what the server says of its files. Only the files and folders that exist are planned.
    /// </summary>
    /// <param name="rootPath">The P4ROOT.</param>
    /// <param name="description">The configuration of the server, or null when it cannot be read.</param>
    /// <param name="logPath">The log of p4d.</param>
    /// <param name="daemonDirectories">
    /// The folders the daemon keeps for the instance: what is inside them is left out of the plan.
    /// </param>
    /// <param name="otherRootPaths">The roots of the other instances.</param>
    /// <param name="protectedDirectories">The folders of the daemon, never reached.</param>
    public static InstanceFilePlan Plan(
        string rootPath,
        ServerDescription? description,
        string logPath,
        IReadOnlyList<string> daemonDirectories,
        IReadOnlyList<string> otherRootPaths,
        IReadOnlyList<string> protectedDirectories)
    {
        string root = Normalize(rootPath);
        string? serverDirectory = ServerLayout.GetServerDirectory(root);
        HashSet<string> directories = new(PathComparer) { root };
        HashSet<string> files = new(PathComparer);
        HashSet<string> candidates = new(PathComparer);
        string? problem = null;

        ServerFileLayout? layout = null;
        if (description is not null)
        {
            try
            {
                layout = ServerFileLocator.Resolve(root, description.Configurables, description.Depots);
            }
            catch (InstanceOperationException)
            {
                // A depot that holds the root: the usual folders are planned instead.
            }
        }

        if (layout is not null)
        {
            foreach (ArchiveSource source in layout.ArchiveSources)
            {
                directories.Add(Normalize(source.Directory));
            }
            AddJournalFiles(layout, files, candidates);
            if (description!.Configurables.TryGetValue(ServerConfigurables.Journal, out string? journal) && !string.IsNullOrWhiteSpace(journal))
            {
                AddFile(Resolve(root, journal), files, candidates);
            }
            AddStructuredLogs(root, description.Configurables, files, candidates);
        }
        else if (serverDirectory is not null)
        {
            foreach (string name in ServerLayout.SiblingFolders)
            {
                directories.Add(Path.Combine(serverDirectory, name));
            }
        }

        AddFile(Normalize(logPath), files, candidates);
        if (serverDirectory is not null)
        {
            candidates.Add(serverDirectory);
            foreach (string name in ServerLayout.SiblingFolders)
            {
                candidates.Add(Path.Combine(serverDirectory, name));
            }
        }

        // What is inside a folder deleted whole is not planned apart, nor what is in the folders of the daemon, such as
        // the log of a server without a logs folder.
        List<string> daemon = daemonDirectories.Select(Normalize).Distinct(PathComparer).ToList();
        string[] planned = [.. directories];
        directories.RemoveWhere(d => !Directory.Exists(d) || planned.Any(o => IsStrictlyInside(d, o)));
        files.RemoveWhere(f => !File.Exists(f)
            || directories.Any(d => ServerFileLocator.Contains(d, f))
            || daemon.Any(d => ServerFileLocator.Contains(d, f)));
        candidates.RemoveWhere(c => daemon.Any(d => ServerFileLocator.Contains(d, c)));

        foreach (string directory in directories)
        {
            problem ??= CheckDirectory(directory, otherRootPaths, protectedDirectories);
        }

        List<string> emptied = PlanEmptiedDirectories(
            candidates.Where(c => !directories.Any(d => ServerFileLocator.Contains(d, c)))
                .Where(c => CheckDirectory(c, otherRootPaths, protectedDirectories) is null),
            directories,
            files);

        return new InstanceFilePlan(
            directories.Order(PathComparer).ToList(),
            files.Order(PathComparer).ToList(),
            emptied,
            layout is not null,
            problem);
    }

    /// <summary>
    /// Deletes what the plan lists: the files, the folders deleted whole, then the folders left empty, the innermost
    /// first. A folder that is not empty by then is kept. What is already gone is skipped, so that an interrupted
    /// deletion can run again.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="cancellationToken">The shutdown of the daemon: the deletion stops between two entries.</param>
    /// <exception cref="IOException">A file or folder could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The daemon may not delete a file or folder.</exception>
    public static void Delete(InstanceFilePlan plan, CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (string file in plan.Files)
        {
            CheckCancellation(ref count, cancellationToken);
            DeleteFile(new FileInfo(file));
        }
        foreach (string directory in plan.Directories)
        {
            DeleteTree(directory, cancellationToken);
        }
        foreach (string directory in plan.EmptiedDirectories)
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    /// <summary>
    /// Deletes a folder with everything inside it, entry by entry, so that the shutdown of the daemon can stop it on a
    /// large one. A link is deleted, never what it points to.
    /// </summary>
    /// <param name="directory">The folder. Nothing happens when it does not exist.</param>
    /// <param name="cancellationToken">The shutdown of the daemon.</param>
    public static void DeleteTree(string directory, CancellationToken cancellationToken)
    {
        DirectoryInfo info = new(directory);
        if (!info.Exists)
        {
            return;
        }
        int count = 0;
        DeleteTree(info, ref count, cancellationToken);
    }

    /// <summary>
    /// Deletes what a folder holds but <paramref name="kept"/>, and the folder itself when nothing is left.
    /// </summary>
    public static void DeleteContents(string directory, string? kept, CancellationToken cancellationToken)
    {
        DirectoryInfo info = new(directory);
        if (!info.Exists)
        {
            return;
        }
        int count = 0;
        foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos())
        {
            if (kept is not null && PathComparer.Equals(Normalize(entry.FullName), Normalize(kept)))
            {
                continue;
            }
            DeleteEntry(entry, ref count, cancellationToken);
        }
        if (!info.EnumerateFileSystemInfos().Any())
        {
            info.Delete();
        }
    }

    private static void DeleteTree(DirectoryInfo directory, ref int count, CancellationToken cancellationToken)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            DeleteEntry(entry, ref count, cancellationToken);
        }
        if (OperatingSystem.IsWindows())
        {
            directory.Attributes &= ~FileAttributes.ReadOnly;
        }
        directory.Delete();
    }

    private static void DeleteEntry(FileSystemInfo entry, ref int count, CancellationToken cancellationToken)
    {
        CheckCancellation(ref count, cancellationToken);
        if (entry is DirectoryInfo directory)
        {
            if (directory.LinkTarget is not null)
            {
                // A link to a folder: only the link goes.
                directory.Delete();
            }
            else
            {
                DeleteTree(directory, ref count, cancellationToken);
            }
        }
        else
        {
            DeleteFile((FileInfo)entry);
        }
    }

    private static void DeleteFile(FileInfo file)
    {
        if (!file.Exists)
        {
            return;
        }
        // p4 makes its tickets file read-only, and Windows does not delete a read-only file.
        if (OperatingSystem.IsWindows() && file.IsReadOnly)
        {
            file.IsReadOnly = false;
        }
        file.Delete();
    }

    private static void CheckCancellation(ref int count, CancellationToken cancellationToken)
    {
        if (++count % CancellationCheckInterval == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    // The checkpoints and the rotated journals of the server, recognized by their names.
    private static void AddJournalFiles(ServerFileLayout layout, HashSet<string> files, HashSet<string> candidates)
    {
        string directory = Normalize(layout.JournalDirectory);
        candidates.Add(directory);
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (ServerFileLocator.ParseJournalFileName(Path.GetFileName(file), layout.JournalPrefix) is not null)
            {
                files.Add(file);
            }
        }
    }

    // The structured logs, serverlog.file.N, with the files p4d rotates them to, such as errors.1.csv.
    private static void AddStructuredLogs(
        string root,
        IReadOnlyDictionary<string, string> configurables,
        HashSet<string> files,
        HashSet<string> candidates)
    {
        foreach ((string name, string value) in configurables)
        {
            if (!name.StartsWith(ServerConfigurables.StructuredLogFilePrefix, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            string log = Resolve(root, value);
            string? directory = Path.GetDirectoryName(log);
            if (directory is null)
            {
                continue;
            }
            candidates.Add(directory);
            if (!Directory.Exists(directory))
            {
                continue;
            }
            string stem = Path.GetFileNameWithoutExtension(log);
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string fileName = Path.GetFileName(file);
                if (string.Equals(fileName, Path.GetFileName(log), StringComparison.Ordinal)
                    || fileName.StartsWith(stem + ".", StringComparison.Ordinal))
                {
                    files.Add(file);
                }
            }
        }
    }

    private static void AddFile(string file, HashSet<string> files, HashSet<string> candidates)
    {
        files.Add(file);
        if (Path.GetDirectoryName(file) is { } directory)
        {
            candidates.Add(directory);
        }
    }

    // The folders that hold nothing once the plan is carried out, the innermost first.
    private static List<string> PlanEmptiedDirectories(
        IEnumerable<string> candidates,
        HashSet<string> directories,
        HashSet<string> files)
    {
        HashSet<string> emptied = new(PathComparer);
        List<string> ordered = [];
        foreach (string candidate in candidates.Where(Directory.Exists).OrderByDescending(c => c.Length))
        {
            bool empty = Directory.EnumerateFileSystemEntries(candidate).All(entry =>
                files.Contains(entry) || directories.Contains(entry) || emptied.Contains(entry));
            if (empty)
            {
                emptied.Add(candidate);
                ordered.Add(candidate);
            }
        }
        return ordered;
    }

    // Why a folder must not be deleted, or null.
    private static string? CheckDirectory(
        string directory,
        IReadOnlyList<string> otherRootPaths,
        IReadOnlyList<string> protectedDirectories)
    {
        if (IsTopLevel(directory))
        {
            return $"{directory} is a root of the file system or a folder right under it: it is not deleted.";
        }
        if (protectedDirectories.FirstOrDefault(p => ServerFileLocator.Contains(directory, p)) is { } held)
        {
            return $"{directory} holds {held}, where the daemon keeps its own files: it is not deleted.";
        }
        if (otherRootPaths.FirstOrDefault(r =>
                ServerFileLocator.Contains(directory, r) || ServerFileLocator.Contains(r, directory)) is { } other)
        {
            return $"{directory} reaches the root {other} of another instance: it is not deleted.";
        }
        return null;
    }

    // A root, such as / or C:\, or a folder right under it, such as /volume1.
    private static bool IsTopLevel(string directory)
    {
        string? parent = Path.GetDirectoryName(directory);
        return parent is null || Path.GetDirectoryName(parent) is null;
    }

    private static bool IsStrictlyInside(string path, string directory)
    {
        return !PathComparer.Equals(path, directory) && ServerFileLocator.Contains(directory, path);
    }

    private static string Resolve(string root, string path)
    {
        string normalized = path.Replace('/', Path.DirectorySeparatorChar);
        return Normalize(Path.IsPathRooted(normalized) ? normalized : Path.Combine(root, normalized));
    }

    private static string Normalize(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}

/// <summary>
/// What the deletion of an instance with its files removes.
/// </summary>
/// <param name="Directories">Folders of the server deleted whole: its root and the folders of its depots.</param>
/// <param name="Files">Files of the server deleted one by one: its checkpoints, journals and logs.</param>
/// <param name="EmptiedDirectories">
/// Folders deleted once the rest is, because nothing else is left in them, the innermost first.
/// </param>
/// <param name="LayoutRead">
/// False when the configuration of the server could not be read: the root and the usual folders next to it are planned.
/// </param>
/// <param name="Problem">Why nothing may be deleted, or null.</param>
public sealed record InstanceFilePlan(
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> EmptiedDirectories,
    bool LayoutRead,
    string? Problem);

