namespace P4ServerManager.Daemon.Services.Backups;

/// <summary>
/// The free space of the volumes, read from the file systems. The volume of a path is its mount point, rather than the
/// root of the path, which is <c>/</c> for any path on Linux, whatever volume holds it.
/// </summary>
public sealed class VolumeSpaceReader : IVolumeSpaceReader
{
    // Links followed in a row before a path is taken as it is: more than that is a loop.
    private const int MaxLinkDepth = 32;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public string GetVolume(string path)
    {
        return FindMountPoint(path, GetMountPoints())
            ?? Path.GetPathRoot(Path.GetFullPath(path))
            ?? path;
    }

    public long? GetAvailableFreeSpace(string volume)
    {
        try
        {
            return new DriveInfo(volume).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The mount points of the machine, such as <c>/</c> and <c>/volume1</c>. Empty when they cannot be read.
    /// </summary>
    private static IReadOnlyList<string> GetMountPoints()
    {
        try
        {
            return DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The mount point that holds <paramref name="path"/>: the longest of <paramref name="mountPoints"/> that holds the
    /// nearest existing folder of the path, once its links are followed. The path itself may not exist yet.
    /// </summary>
    /// <param name="path">A folder, which may not exist yet.</param>
    /// <param name="mountPoints">The mount points, from <see cref="GetMountPoints"/>.</param>
    /// <returns>The mount point, or null when none holds the path.</returns>
    public static string? FindMountPoint(string path, IEnumerable<string> mountPoints)
    {
        string resolved = ResolveExisting(path, 0);
        return mountPoints
            .Where(mountPoint => Holds(mountPoint, resolved))
            .MaxBy(mountPoint => Path.TrimEndingDirectorySeparator(mountPoint).Length);
    }

    /// <summary>
    /// The nearest existing folder of <paramref name="path"/>, each link on the way replaced by its target.
    /// </summary>
    private static string ResolveExisting(string path, int depth)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? full;
        string current = root;
        string[] segments = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            string next = Path.Combine(current, segment);
            if (!Directory.Exists(next))
            {
                break;
            }

            current = depth < MaxLinkDepth
                && new DirectoryInfo(next).LinkTarget is not null
                && Directory.ResolveLinkTarget(next, returnFinalTarget: true) is { } target
                    ? ResolveExisting(target.FullName, depth + 1)
                    : next;
        }
        return current;
    }

    private static bool Holds(string mountPoint, string path)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountPoint));
        string child = Path.TrimEndingDirectorySeparator(path);
        if (child.Equals(parent, PathComparison))
        {
            return true;
        }

        // A root such as "/" or "C:\" keeps its separator once trimmed.
        string prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, PathComparison);
    }
}

/// <summary>
/// The free space of the volumes of the machine, by mount point.
/// </summary>
public interface IVolumeSpaceReader
{
    /// <summary>
    /// The volume that holds <paramref name="path"/>, which may not exist yet: its mount point, or the root of the path
    /// when no mount point is known.
    /// </summary>
    string GetVolume(string path);

    /// <summary>
    /// The bytes free on a volume from <see cref="GetVolume"/>, or null when it cannot be read.
    /// </summary>
    long? GetAvailableFreeSpace(string volume);
}
