using P4ServerManager.Backup.Manifests;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Perforce.Parsing;

namespace P4ServerManager.Daemon.Services.Backups.Restore;

/// <summary>
/// Places the files of a backup point from the layout of the restored database: the manifest gives each file relative
/// to <c>server.depot.root</c>, to the folder of a depot kept elsewhere, to the root or to the folder of the journals.
/// </summary>
public static class RestorePlanner
{
    private const string JournalsPrefix = "journals/";
    private const string RootPrefix = "root/";
    private const string ServerIdPath = RootPrefix + ServerLayout.ServerIdFile;
    private const string LicensePath = RootPrefix + "license";

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Places the files of a point.
    /// </summary>
    /// <param name="entries">The files of the point.</param>
    /// <param name="original">
    /// The layout the restored database describes, with its configurables as they were backed up: it tells where each
    /// depot kept its files, so what each path of the manifest is.
    /// </param>
    /// <param name="target">
    /// The layout of the restored server: the same as <paramref name="original"/> when the configuration is kept, or the
    /// one of the new instance.
    /// </param>
    /// <param name="depots">Every depot of the restored database.</param>
    /// <param name="options">How to place the files.</param>
    /// <exception cref="InvalidDataException">A path of the manifest goes nowhere, or out of its folder.</exception>
    public static RestorePlan Plan(
        IReadOnlyList<BackupManifestEntry> entries,
        ServerFileLayout original,
        ServerFileLayout target,
        IReadOnlyList<DepotRecord> depots,
        RestorePlanOptions options)
    {
        // Where each start of path goes: archives under server.depot.root, and each depot kept elsewhere either in its
        // own folder or, relocated, under server.depot.root.
        Dictionary<string, string> bases = new(StringComparer.Ordinal)
        {
            [ServerFileLocator.ArchivesPrefix] = target.DepotRoot,
        };
        Dictionary<string, (string From, string To)> moved = new(StringComparer.Ordinal);
        foreach (ArchiveSource source in original.ArchiveSources)
        {
            if (!source.Prefix.StartsWith(ServerFileLocator.DepotsPrefix + "/", StringComparison.Ordinal))
            {
                continue;
            }

            string name = source.Prefix[(ServerFileLocator.DepotsPrefix.Length + 1)..];
            string directory = options.RelocateDepots
                ? Path.Combine(target.DepotRoot, CheckSegment(name, source.Prefix))
                : source.Directory;
            bases[source.Prefix] = directory;
            moved[source.Prefix] = (source.Directory, directory);
        }

        List<DepotRelocation> relocations = options.RelocateDepots
            ? GetRelocations(original, target, depots, moved.Values)
            : [];

        List<RestoreFile> metadata = [];
        List<RestoreFile> files = [];
        HashSet<string> directories = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (BackupManifestEntry entry in entries)
        {
            if (BackupFileRoles.IsMetadata(entry.Role))
            {
                if (!entry.Path.StartsWith(JournalsPrefix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The point has a {entry.Role} outside the journals: {entry.Path}.");
                }
                metadata.Add(new RestoreFile(entry, Place(target.JournalDirectory, entry.Path[JournalsPrefix.Length..], entry.Path)));
                directories.Add(target.JournalDirectory);
                continue;
            }

            if (entry.Path.StartsWith(RootPrefix, StringComparison.Ordinal))
            {
                if ((!options.RestoreServerId && entry.Path == ServerIdPath)
                    || (!options.RestoreLicense && entry.Path == LicensePath))
                {
                    continue;
                }
                RestoreFile rootFile = new(entry, Place(target.RootPath, entry.Path[RootPrefix.Length..], entry.Path));
                files.Add(rootFile);
                directories.Add(Path.GetDirectoryName(rootFile.Path)!);
                continue;
            }

            (string prefix, string baseDirectory) = FindBase(bases, entry.Path);
            RestoreFile file = new(entry, Place(baseDirectory, entry.Path[(prefix.Length + 1)..], entry.Path));
            files.Add(file);
            directories.Add(baseDirectory);
        }

        foreach (string directory in directories)
        {
            if (options.ForbiddenDirectories.FirstOrDefault(f => ServerFileLocator.Contains(f, directory)
                || ServerFileLocator.Contains(directory, f)) is { } forbidden)
            {
                throw new InvalidDataException(
                    $"The point would restore files to {directory}, which overlaps {forbidden}, a folder of another instance.");
            }
        }

        List<string> warnings = target.ObjectStoreDepots
            .Select(d => $"Depot {d.Name} keeps its archives in an object store: they are not in the backup, and stay where the store keeps them.")
            .ToList();
        return new RestorePlan(metadata, files, relocations, directories.Order(StringComparer.Ordinal).ToList(), warnings);
    }

    /// <summary>
    /// The depots whose files are not where their map says once the files are placed: the depots kept outside
    /// <c>server.depot.root</c>, those inside their folders, and those whose map points to the old folder of
    /// <c>server.depot.root</c>.
    /// </summary>
    private static List<DepotRelocation> GetRelocations(
        ServerFileLayout original,
        ServerFileLayout target,
        IReadOnlyList<DepotRecord> depots,
        IEnumerable<(string From, string To)> moved)
    {
        List<(string From, string To)> moves = [.. moved];
        List<DepotRelocation> relocations = [];
        foreach (DepotRecord depot in depots)
        {
            if (depot.Type == DepotRecord.RemoteType || depot.ObjectAddress.Length > 0)
            {
                continue;
            }

            string originalDirectory = ServerFileLocator.ResolveMap(original.DepotRoot, depot.Map);
            string? placed = null;
            foreach ((string from, string to) in moves)
            {
                if (ServerFileLocator.Contains(from, originalDirectory))
                {
                    placed = Path.Combine(to, Path.GetRelativePath(from, originalDirectory));
                    break;
                }
            }
            if (placed is null && ServerFileLocator.Contains(original.DepotRoot, originalDirectory))
            {
                placed = Path.Combine(target.DepotRoot, Path.GetRelativePath(original.DepotRoot, originalDirectory));
            }
            if (placed is null)
            {
                continue;
            }

            placed = Path.GetFullPath(placed);
            if (string.Equals(
                Path.TrimEndingDirectorySeparator(ServerFileLocator.ResolveMap(target.DepotRoot, depot.Map)),
                Path.TrimEndingDirectorySeparator(placed),
                PathComparison))
            {
                continue;
            }

            string relative = Path.GetRelativePath(target.DepotRoot, placed).Replace(Path.DirectorySeparatorChar, '/');
            relocations.Add(new DepotRelocation(depot.Name, relative == "." ? "..." : $"{relative}/..."));
        }
        return relocations;
    }

    private static (string Prefix, string Directory) FindBase(Dictionary<string, string> bases, string path)
    {
        string? best = null;
        foreach (string prefix in bases.Keys)
        {
            if (path.StartsWith(prefix + "/", StringComparison.Ordinal) && (best is null || prefix.Length > best.Length))
            {
                best = prefix;
            }
        }
        return best is null
            ? throw new InvalidDataException($"The restored server keeps no folder for {path}.")
            : (best, bases[best]);
    }

    /// <summary>
    /// The full path of <paramref name="relative"/> under <paramref name="baseDirectory"/>, refusing any path that would
    /// leave it.
    /// </summary>
    private static string Place(string baseDirectory, string relative, string manifestPath)
    {
        string[] segments = relative.Split('/');
        foreach (string segment in segments)
        {
            CheckSegment(segment, manifestPath);
        }

        string path = Path.GetFullPath(Path.Combine([baseDirectory, .. segments]));
        if (!ServerFileLocator.Contains(baseDirectory, path) || ServerFileLocator.Contains(path, baseDirectory))
        {
            throw new InvalidDataException($"The point has a file that leaves its folder: {manifestPath}.");
        }
        return path;
    }

    private static string CheckSegment(string segment, string manifestPath)
    {
        if (segment.Length == 0
            || segment is "." or ".."
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException($"The point has a path this server cannot write: {manifestPath}.");
        }
        return segment;
    }
}

/// <summary>
/// A file of a backup point, and where the restore writes it.
/// </summary>
public sealed record RestoreFile(BackupManifestEntry Entry, string Path);

/// <summary>
/// A depot whose files the restore puts elsewhere than its map says: its map must change to <paramref name="Map"/>.
/// </summary>
/// <param name="Name">Name of the depot.</param>
/// <param name="Map">Its new map, relative to <c>server.depot.root</c>, such as <c>assets/...</c>.</param>
public sealed record DepotRelocation(string Name, string Map);

/// <summary>
/// Where each file of a backup point goes.
/// </summary>
/// <param name="Metadata">The checkpoint, its checksum and the journals, in the folder of the journals.</param>
/// <param name="Files">The archives and the files of the root.</param>
/// <param name="Relocations">Depots whose map must change once the server runs.</param>
/// <param name="Directories">Folders the files go to: every one is created, and the disk under it checked.</param>
/// <param name="Warnings">What the point cannot give back.</param>
public sealed record RestorePlan(
    IReadOnlyList<RestoreFile> Metadata,
    IReadOnlyList<RestoreFile> Files,
    IReadOnlyList<DepotRelocation> Relocations,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Warnings);

/// <summary>
/// How to place the files of a point.
/// </summary>
/// <param name="RelocateDepots">
/// Whether to put the depots kept outside <c>server.depot.root</c> under it, in a folder named after them: for a new
/// instance, whose files must not go where the instance that was backed up keeps its own.
/// </param>
/// <param name="RestoreServerId">Whether to restore <c>server.id</c>: not for a new instance, which has its own.</param>
/// <param name="RestoreLicense">Whether to restore the license file.</param>
/// <param name="ForbiddenDirectories">Folders no file may go to, such as the ones of the other instances.</param>
public sealed record RestorePlanOptions(
    bool RelocateDepots,
    bool RestoreServerId,
    bool RestoreLicense,
    IReadOnlyList<string> ForbiddenDirectories);
