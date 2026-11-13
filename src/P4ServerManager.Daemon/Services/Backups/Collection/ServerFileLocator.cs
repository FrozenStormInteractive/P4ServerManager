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
/// Reads the layout of a server with p4d, on its root. It works whether p4d runs or not.
/// </summary>
public sealed partial class ServerFileLocator(IP4ServerBuildCache buildCache, IProcessRunner processRunner)
{
    private static readonly TimeSpan DumpTimeout = TimeSpan.FromMinutes(5);

    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    /// <param name="cancellationToken">Aborts the reads.</param>
    public async Task<ServerFileLayout> ReadLayoutAsync(P4ServerInstance instance, CancellationToken cancellationToken)
    {
        string rootPath = Path.GetFullPath(instance.RootPath);
        string? serverId = await ServerLayout.TryReadServerIdAsync(rootPath, cancellationToken);
        IReadOnlyDictionary<string, string> configurables = CheckpointRecordParser.ReadConfigurables(
            await DumpAsync(instance, "db.config", cancellationToken),
            serverId);
        IReadOnlyList<DepotRecord> depots = CheckpointRecordParser.ReadDepots(
            await DumpAsync(instance, "db.depot", cancellationToken));
        return Resolve(rootPath, configurables, depots);
    }

    /// <summary>
    /// Number of the journal p4d writes to: the next rotation gives it this number.
    /// </summary>
    public async Task<int> ReadJournalCounterAsync(P4ServerInstance instance, CancellationToken cancellationToken)
    {
        string? value = CheckpointRecordParser.ReadCounter(
            await DumpAsync(instance, "db.counters", cancellationToken),
            "journal");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : 0;
    }

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

        string depotRoot = configurables.TryGetValue("server.depot.root", out string? configured)
            && !string.IsNullOrWhiteSpace(configured)
                ? ResolvePath(rootPath, configured)
                : rootPath;

        List<DepotLocation> locations = [];
        foreach (DepotRecord depot in depots)
        {
            string map = depot.Map.Trim();
            if (map.EndsWith("...", StringComparison.Ordinal))
            {
                map = map[..^3];
            }
            map = map.TrimEnd('/', '\\');
            string directory = map.Length == 0 ? depotRoot : ResolvePath(depotRoot, map);
            locations.Add(new DepotLocation(depot.Name, directory));
        }

        return new ServerFileLayout(rootPath, journalDirectory, journalPrefix, locations);
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

    private async Task<string> DumpAsync(P4ServerInstance instance, string table, CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(instance.Build!),
                [.. P4ServerLauncher.GetRootArguments(instance), "-jd", "-", table],
                Timeout: DumpTimeout),
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
/// journals in its root, or its depots anywhere.
/// </summary>
/// <param name="RootPath">The P4ROOT.</param>
/// <param name="JournalDirectory">Folder of the checkpoints and of the rotated journals.</param>
/// <param name="JournalPrefix">
/// Start of their file names, from <c>journalPrefix</c>, such as <c>main</c> for <c>main.ckp.12.gz</c>. Null without
/// <c>journalPrefix</c>: the files are then <c>checkpoint.12</c> and <c>journal.11</c> in the root.
/// </param>
/// <param name="Depots">Folders of the depots that keep files.</param>
public sealed record ServerFileLayout(
    string RootPath,
    string JournalDirectory,
    string? JournalPrefix,
    IReadOnlyList<DepotLocation> Depots);

/// <param name="Name">Name of the depot.</param>
/// <param name="Directory">Absolute folder of its files.</param>
public sealed record DepotLocation(string Name, string Directory);

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
