using System.Collections.Concurrent;
using System.Globalization;

using P4ServerManager.Backup.Crypto;
using P4ServerManager.Backup.Manifests;
using P4ServerManager.Backup.Repository;
using P4ServerManager.Backup.Targets;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Destinations;

namespace P4ServerManager.Daemon.Services.Backups.Restore;

/// <summary>
/// A backup point, with what its header tells.
/// </summary>
/// <param name="Snapshot">Where the manifest is.</param>
/// <param name="Header">What the point is.</param>
public sealed record BackupPoint(BackupSnapshotInfo Snapshot, BackupManifestHeader Header)
{
    public BackupKind Kind => string.Equals(Header.Kind, "incremental", StringComparison.Ordinal)
        ? BackupKind.Incremental
        : BackupKind.Full;

    public int? CheckpointNumber => GetNumber(BackupLabels.CheckpointNumber);

    public int? JournalNumber => GetNumber(BackupLabels.JournalNumber);

    public string? P4dVersion => Header.Labels.GetValueOrDefault(BackupLabels.P4dVersion);

    public bool Unicode => GetFlag(BackupLabels.Unicode, false);

    public bool CaseSensitive => GetFlag(BackupLabels.CaseSensitive, true);

    public string? ServerId => Header.Labels.GetValueOrDefault(BackupLabels.ServerId);

    /// <summary>
    /// The port of the instance that was backed up, unknown for the points of the first backups.
    /// </summary>
    public int? Port => GetNumber(BackupLabels.Port);

    private int? GetNumber(string label)
    {
        return Header.Labels.TryGetValue(label, out string? value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? number
                : null;
    }

    private bool GetFlag(string label, bool fallback)
    {
        return Header.Labels.TryGetValue(label, out string? value) && bool.TryParse(value, out bool flag)
            ? flag
            : fallback;
    }
}

/// <summary>
/// Lists the backup points stored at a destination, from the destination itself. Only the header of each manifest is
/// read; manifests never change, so the headers are kept in memory.
/// </summary>
public sealed class BackupPointService(
    BackupDestinationService destinationService,
    ILogger<BackupPointService> logger)
{
    private const int ParallelReads = 8;

    private readonly ConcurrentDictionary<(Guid RepositoryId, string Key), BackupManifestHeader> _headers = new();

    /// <summary>
    /// The points of the destination, or of one instance, newest first. A point whose header cannot be read is left out.
    /// </summary>
    /// <exception cref="BackupDestinationException">The destination cannot be opened.</exception>
    public async Task<IReadOnlyList<BackupPoint>> ListAsync(
        BackupDestination destination,
        Guid? sourceId,
        CancellationToken cancellationToken)
    {
        await using OpenedBackupDestination opened = await destinationService.OpenAsync(destination, cancellationToken);
        List<BackupSnapshotInfo> snapshots = [];
        await foreach (BackupSnapshotInfo snapshot in opened.Repository.ListSnapshotsAsync(sourceId, cancellationToken))
        {
            snapshots.Add(snapshot);
        }

        ConcurrentBag<BackupPoint> points = [];
        await Parallel.ForEachAsync(
            snapshots,
            new ParallelOptions { MaxDegreeOfParallelism = ParallelReads, CancellationToken = cancellationToken },
            async (snapshot, token) =>
            {
                if (await TryReadHeaderAsync(opened, snapshot, token) is { } header)
                {
                    points.Add(new BackupPoint(snapshot, header));
                }
            });

        return points
            .OrderByDescending(p => p.Snapshot.CreatedAt)
            .ThenByDescending(p => p.Snapshot.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// One point of the destination.
    /// </summary>
    /// <exception cref="BackupObjectNotFoundException">The destination has no such point.</exception>
    public async Task<BackupPoint> GetAsync(
        OpenedBackupDestination opened,
        string key,
        CancellationToken cancellationToken)
    {
        if (!BackupRepository.TryParseManifestKey(key, out BackupSnapshotInfo? snapshot))
        {
            throw new BackupObjectNotFoundException(key);
        }
        BackupManifestHeader header = _headers.TryGetValue((opened.Repository.RepositoryId, key), out BackupManifestHeader? cached)
            ? cached
            : await opened.Repository.ReadManifestHeaderAsync(key, cancellationToken);
        _headers.TryAdd((opened.Repository.RepositoryId, key), header);
        return new BackupPoint(snapshot, header);
    }

    private async Task<BackupManifestHeader?> TryReadHeaderAsync(
        OpenedBackupDestination opened,
        BackupSnapshotInfo snapshot,
        CancellationToken cancellationToken)
    {
        (Guid, string) cacheKey = (opened.Repository.RepositoryId, snapshot.Key);
        if (_headers.TryGetValue(cacheKey, out BackupManifestHeader? cached))
        {
            return cached;
        }

        try
        {
            BackupManifestHeader header = await opened.Repository.ReadManifestHeaderAsync(snapshot.Key, cancellationToken);
            _headers.TryAdd(cacheKey, header);
            return header;
        }
        catch (Exception exception) when (exception is BackupIntegrityException or BackupObjectNotFoundException)
        {
            logger.LogWarning(
                exception,
                "Backup point {Key} at destination {Destination} cannot be read",
                snapshot.Key,
                opened.Destination.Name);
            return null;
        }
    }
}
