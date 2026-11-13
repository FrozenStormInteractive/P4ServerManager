namespace P4ServerManager.Contracts;

/// <summary>
/// A backup point stored at a destination, read from the destination itself rather than from the history of this
/// daemon: it lists the points of instances this daemon never knew, such as after a reinstallation.
/// </summary>
/// <param name="Key">Key of the manifest of the point: what a restore asks for.</param>
/// <param name="Id">Identifier of the point, the one of the backup that made it.</param>
/// <param name="SourceId">Identifier of the instance that was backed up.</param>
/// <param name="SourceName">Its name at the time of the backup.</param>
/// <param name="Kind">Full or incremental. Both are restorable on their own.</param>
/// <param name="CreatedAt">When the backup began.</param>
/// <param name="CheckpointNumber">The checkpoint the point builds on.</param>
/// <param name="JournalNumber">The last journal of the point, for an incremental backup.</param>
/// <param name="P4dVersion">The p4d build that ran the instance, such as <c>2025.1/2799662</c>.</param>
/// <param name="Unicode">Whether the server was in Unicode mode.</param>
/// <param name="CaseSensitive">Whether the server was case-sensitive.</param>
/// <param name="ServerId">The server ID of the instance.</param>
public sealed record BackupPointDto(
    string Key,
    Guid Id,
    Guid SourceId,
    string SourceName,
    BackupKind Kind,
    DateTimeOffset CreatedAt,
    int? CheckpointNumber,
    int? JournalNumber,
    string? P4dVersion,
    bool Unicode,
    bool CaseSensitive,
    string? ServerId);
