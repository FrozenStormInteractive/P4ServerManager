using System.Globalization;
using System.Text.RegularExpressions;

using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Backups.Collection;

/// <summary>
/// Reads the layout of a server with p4d, on its root, without any user: it works whether p4d runs or not. The
/// configurables come from <c>p4d -cshow</c>. The depots come from a dump of <c>db.depot</c>, read strictly, and only to
/// check that every depot keeps its files under <c>server.depot.root</c>: the backup takes that folder whole, so it
/// needs no list of the depots, and misses none.
/// </summary>
public sealed partial class ServerFileLocator(IP4ServerBuildCache buildCache, IProcessRunner processRunner)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(5);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    /// <param name="cancellationToken">Aborts the reads.</param>
    /// <exception cref="InstanceOperationException">p4d printed what this reader does not know, or a depot keeps its
    /// files where the backup cannot take them.</exception>
    public async Task<ServerFileLayout> ReadLayoutAsync(P4ServerInstance instance, CancellationToken cancellationToken)
    {
        string rootPath = Path.GetFullPath(instance.RootPath);
        string? serverId = await ServerLayout.TryReadServerIdAsync(rootPath, cancellationToken);
        try
        {
            IReadOnlyDictionary<string, string> configurables = ConfigShowParser.Parse(
                await RunP4dAsync(instance, ["-cshow"], cancellationToken),
                serverId);
            IReadOnlyList<DepotRecord> depots = DepotRecord.ReadAll(
                await RunP4dAsync(instance, ["-jd", "-", "db.depot"], cancellationToken));
            return Resolve(rootPath, configurables, depots);
        }
        catch (InvalidDataException exception)
        {
            throw new InstanceOperationException(
                $"The layout of the server cannot be read for sure, so it is not backed up. {exception.Message}");
        }
    }

    /// <summary>
    /// Where the files of the server are, from its configurables, once every depot is checked.
    /// </summary>
    /// <param name="rootPath">The P4ROOT, absolute.</param>
    /// <param name="configurables">The configurables of the server.</param>
    /// <param name="depots">Every depot of the server.</param>
    /// <exception cref="InstanceOperationException">A depot keeps its files outside <c>server.depot.root</c>, in a
    /// folder that holds the root, or in an object store.</exception>
    public static ServerFileLayout Resolve(
        string rootPath,
        IReadOnlyDictionary<string, string> configurables,
        IReadOnlyList<DepotRecord> depots)
    {
        string journalDirectory = rootPath;
        string? journalPrefix = null;
        if (configurables.TryGetValue("journalPrefix", out string? prefix) && !string.IsNullOrWhiteSpace(prefix))
        {
            string resolved = ResolvePath(rootPath, prefix);
            journalDirectory = Path.GetDirectoryName(resolved) ?? rootPath;
            journalPrefix = Path.GetFileName(resolved);
        }

        // Without server.depot.root, p4d places the depots in its root.
        string depotRoot = configurables.TryGetValue("server.depot.root", out string? configured)
            && !string.IsNullOrWhiteSpace(configured)
                ? ResolvePath(rootPath, configured)
                : rootPath;

        List<string> depotDirectories = [];
        foreach (DepotRecord depot in depots)
        {
            if (depot.Type == DepotRecord.RemoteType)
            {
                continue;
            }

            if (depot.ObjectAddress.Length > 0)
            {
                throw new InstanceOperationException(
                    $"Depot {depot.Name} keeps its archives in an object store ({depot.ObjectAddress}), "
                    + "which the backup cannot reach.");
            }

            string directory = ResolveMap(depotRoot, depot.Map);
            if (!Contains(depotRoot, directory))
            {
                throw new InstanceOperationException(
                    $"Depot {depot.Name} keeps its files in {directory}, outside server.depot.root ({depotRoot}): "
                    + "the backup would miss them. Move the depot under server.depot.root.");
            }
            if (Contains(directory, rootPath))
            {
                throw new InstanceOperationException(
                    $"Depot {depot.Name} keeps its files in {directory}, which holds the root of the server: "
                    + "such a layout cannot be backed up.");
            }
            depotDirectories.Add(directory);
        }

        // The backup takes server.depot.root whole, unless it holds the root or the checkpoints of the server: it then
        // takes the folder of each depot, every one checked above.
        bool takeWhole = !Contains(depotRoot, rootPath) && !SamePath(depotRoot, journalDirectory);
        IReadOnlyList<string> archiveDirectories = takeWhole
            ? [depotRoot]
            : depotDirectories.Distinct(PathComparer).ToList();
        IReadOnlyList<string> excluded = takeWhole && Contains(depotRoot, journalDirectory)
            ? [journalDirectory]
            : [];

        return new ServerFileLayout(rootPath, journalDirectory, journalPrefix, depotRoot, archiveDirectories, excluded);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="directory"/> or inside it.
    /// </summary>
    public static bool Contains(string directory, string path)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return child.Equals(parent, PathComparison)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>
    /// What a file of the folder of the journals is, from its name.
    /// </summary>
    public static JournalFile? ParseJournalFileName(string fileName, string? journalPrefix)
    {
        Match match = journalPrefix is null
            ? DefaultJournalFile().Match(fileName)
            : PrefixedJournalFile().Match(fileName) is { Success: true } prefixed
                && prefixed.Groups["prefix"].Value == journalPrefix
                    ? prefixed
                    : Match.Empty;
        if (!match.Success
            || !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return null;
        }

        JournalFileKind kind = match.Groups["md5"].Success
            ? JournalFileKind.Checksum
            : match.Groups["kind"].Value is "ckp" or "checkpoint" ? JournalFileKind.Checkpoint : JournalFileKind.Journal;
        return new JournalFile(kind, number);
    }

    private static string ResolvePath(string basePath, string path)
    {
        string normalized = path.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(basePath, normalized));
    }

    private static bool SamePath(string a, string b)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a))
            .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), PathComparison);
    }

    /// <summary>
    /// The folder of a depot map, such as <c>depot/...</c>: relative to <paramref name="depotRoot"/>, or absolute.
    /// </summary>
    private static string ResolveMap(string depotRoot, string map)
    {
        string trimmed = map.Trim();
        if (trimmed.EndsWith("...", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3];
        }
        trimmed = trimmed.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? depotRoot : ResolvePath(depotRoot, trimmed);
    }

    private async Task<string> RunP4dAsync(
        P4ServerInstance instance,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(instance.Build!),
                [.. P4ServerLauncher.GetRootArguments(instance), .. arguments],
                Timeout: ReadTimeout),
            cancellationToken);
        return result.StandardOutput;
    }

    [GeneratedRegex(@"^(?<prefix>.+)\.(?<kind>ckp|jnl)\.(?<number>\d+)(\.gz)?(?<md5>\.md5)?$")]
    private static partial Regex PrefixedJournalFile();

    [GeneratedRegex(@"^(?<kind>checkpoint|journal)\.(?<number>\d+)(\.gz)?(?<md5>\.md5)?$")]
    private static partial Regex DefaultJournalFile();
}

/// <summary>
/// Where a server keeps its files, read from its own configuration rather than assumed: an imported server may keep its
/// journals in its root, or its depots anywhere under <c>server.depot.root</c>.
/// </summary>
/// <param name="RootPath">The P4ROOT.</param>
/// <param name="JournalDirectory">Folder of the checkpoints and of the rotated journals.</param>
/// <param name="JournalPrefix">
/// Start of their file names, from <c>journalPrefix</c>, such as <c>main</c> for <c>main.ckp.12.gz</c>. Null without
/// <c>journalPrefix</c>: the files are then <c>checkpoint.12</c> and <c>journal.11</c> in the root.
/// </param>
/// <param name="DepotRoot">
/// The folder of <c>server.depot.root</c>, or the root without it: the archives are backed up relative to it.
/// </param>
/// <param name="ArchiveDirectories">
/// Folders the backup takes whole: <see cref="DepotRoot"/> itself, or the folder of each depot when
/// <see cref="DepotRoot"/> also holds the root or the checkpoints.
/// </param>
/// <param name="ExcludedDirectories">
/// Folders inside <see cref="ArchiveDirectories"/> that hold no archive: the folder of the checkpoints and journals,
/// which the backup takes apart.
/// </param>
public sealed record ServerFileLayout(
    string RootPath,
    string JournalDirectory,
    string? JournalPrefix,
    string DepotRoot,
    IReadOnlyList<string> ArchiveDirectories,
    IReadOnlyList<string> ExcludedDirectories);

public enum JournalFileKind
{
    Checkpoint,

    /// <summary>
    /// The MD5 p4d writes next to a checkpoint.
    /// </summary>
    Checksum,

    Journal,
}

/// <param name="Kind">What the file is.</param>
/// <param name="Number">Its number: the one of the checkpoint, or of the journal.</param>
public sealed record JournalFile(JournalFileKind Kind, int Number);
