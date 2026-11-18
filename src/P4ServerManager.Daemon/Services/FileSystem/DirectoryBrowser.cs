namespace P4ServerManager.Daemon.Services.FileSystem;

/// <summary>
/// Reads the folders of the machine of the daemon, so that the web UI can offer a folder picker for the paths it asks
/// for. Only folder names and permissions are read, never files nor their content, and nothing is written.
/// </summary>
public static class DirectoryBrowser
{
    // Not recursive: only the folder that is listed can be denied, and that is reported.
    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false,
    };

    /// <summary>
    /// The roots of the file system: <c>/</c>, or the ready drives on Windows.
    /// </summary>
    public static IReadOnlyList<string> GetRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ["/"];
        }

        return DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => drive.RootDirectory.FullName)
            .ToList();
    }

    /// <summary>
    /// The absolute path, without <c>.</c> or <c>..</c> segments nor a trailing separator except for a root.
    /// </summary>
    public static string Normalize(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>
    /// Lists the subfolders of a folder.
    /// </summary>
    /// <param name="path">Absolute path of the folder; null for the first root.</param>
    /// <exception cref="ArgumentException">The path is not absolute, or not a valid path.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist, or it is a file.</exception>
    /// <exception cref="UnauthorizedAccessException">The daemon may not read the folder.</exception>
    public static DirectoryListing List(string? path)
    {
        if (path is not null && !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The folder is an absolute path.", nameof(path));
        }

        IReadOnlyList<string> roots = GetRoots();
        string directory = path is null ? GetDefaultRoot(roots) : Normalize(path);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(File.Exists(directory)
                ? $"{directory} is a file, not a folder."
                : $"The folder {directory} does not exist.");
        }

        List<DirectoryEntry> directories = Directory.EnumerateDirectories(directory, "*", ListingOptions)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d, StringComparer.Ordinal)
            .Select(ToEntry)
            .ToList();

        return new DirectoryListing(
            directory,
            GetAncestors(directory).Select(ToEntry).ToList(),
            directories,
            roots.Select(ToEntry).ToList());
    }

    /// <summary>
    /// An entry next to the path whose name differs from the name of the path by its case only, such as <c>main</c>
    /// for <c>/srv/Main</c>. Both can live side by side on a file system that tells the case apart, but they are easy to
    /// mix up, and a client that ignores the case, such as Windows over SMB, may open one for the other. On a file system
    /// that ignores the case, the path names that entry itself: it is not reported.
    /// </summary>
    /// <param name="path">Absolute path, which may not exist.</param>
    /// <returns>The path of the entry, or null when there is none or the parent folder cannot be read.</returns>
    public static string? FindCaseVariant(string path)
    {
        string fullPath = Normalize(path);
        string? parent = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileName(fullPath);
        if (parent is null || name.Length == 0 || !Directory.Exists(parent))
        {
            return null;
        }

        List<string> matches;
        try
        {
            matches = Directory.EnumerateFileSystemEntries(
                    parent,
                    name,
                    new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, MatchType = MatchType.Simple })
                // The name may hold the wildcards of the pattern, '*' and '?'.
                .Where(m => string.Equals(Path.GetFileName(m), name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        bool exact = matches.Any(m => string.Equals(Path.GetFileName(m), name, StringComparison.Ordinal));
        string? variant = matches.FirstOrDefault(m => !string.Equals(Path.GetFileName(m), name, StringComparison.Ordinal));
        // Without an entry of the exact name, a path that exists resolves to the variant: they are the same entry.
        if (variant is null || (!exact && Path.Exists(fullPath)))
        {
            return null;
        }
        return variant;
    }

    /// <summary>
    /// The path as the API gives it, with '/' as the separator on every platform: the web UI handles a single kind of
    /// path. Windows reads it back as it is. Elsewhere, '\' is a character of a name and is kept.
    /// </summary>
    public static string ToApiPath(string path)
    {
        return OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
    }

    /// <summary>
    /// The name of a folder, or its path for a root.
    /// </summary>
    public static string GetName(string path)
    {
        string name = Path.GetFileName(path);
        return name.Length > 0 ? name : path;
    }

    private static DirectoryEntry ToEntry(string path)
    {
        return new DirectoryEntry(path, DirectoryAccess.CanCreateEntries(path));
    }

    private static string GetDefaultRoot(IReadOnlyList<string> roots)
    {
        if (OperatingSystem.IsWindows() && Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } systemDrive)
        {
            return systemDrive;
        }

        return roots.Count > 0 ? roots[0] : Path.GetPathRoot(Environment.CurrentDirectory)!;
    }

    private static List<string> GetAncestors(string directory)
    {
        List<string> ancestors = [];
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            ancestors.Add(current);
        }

        ancestors.Reverse();
        return ancestors;
    }
}

/// <summary>
/// A folder of the machine of the daemon.
/// </summary>
/// <param name="Path">Absolute path of the folder.</param>
/// <param name="Writable">Whether the daemon may create files and folders in it.</param>
public sealed record DirectoryEntry(string Path, bool Writable);

/// <summary>
/// A folder and its subfolders, as <see cref="DirectoryBrowser"/> reads them.
/// </summary>
/// <param name="Path">Absolute path of the folder, normalized.</param>
/// <param name="Ancestors">The folders from its root down to the folder itself.</param>
/// <param name="Directories">Its subfolders, by name.</param>
/// <param name="Roots">The roots of the file system.</param>
public sealed record DirectoryListing(
    string Path,
    IReadOnlyList<DirectoryEntry> Ancestors,
    IReadOnlyList<DirectoryEntry> Directories,
    IReadOnlyList<DirectoryEntry> Roots)
{
    /// <summary>
    /// Folder that holds it, or null for a root.
    /// </summary>
    public string? Parent => Ancestors.Count > 1 ? Ancestors[^2].Path : null;

    /// <summary>
    /// Whether the daemon may create files and folders in the folder.
    /// </summary>
    public bool Writable => Ancestors[^1].Writable;
}
