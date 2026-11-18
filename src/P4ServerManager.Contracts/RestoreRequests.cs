using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Contracts;

/// <summary>
/// A backup point to restore: a destination, and the key of the manifest of the point there.
/// </summary>
public sealed record BackupPointReference
{
    /// <summary>
    /// The destination that holds the point.
    /// </summary>
    public Guid DestinationId { get; init; }

    /// <summary>
    /// Key of the manifest of the point, from <see cref="BackupPointDto.Key"/> or
    /// <see cref="BackupRunDestinationDto.SnapshotKey"/>.
    /// </summary>
    [Required]
    public required string SnapshotKey { get; init; }
}

/// <summary>
/// Makes a new instance from a backup point. Its case handling and Unicode mode are the ones of the point; its server
/// ID is its name, as for any instance. The server keeps the users and the passwords of the point.
/// </summary>
public sealed record RestoreInstanceRequest
{
    /// <summary>
    /// Name of the instance, and server ID of the restored server.
    /// </summary>
    [Required]
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// TCP port p4d listens on.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// Release of p4d, such as <c>r25.1</c>. Null for the release of the point. It cannot be older.
    /// </summary>
    public string? Release { get; init; }

    /// <summary>
    /// Exact build of <see cref="Release"/> in the archive. Null for the build of the point when the archive has it and
    /// no release is given, otherwise for the latest build of the release.
    /// </summary>
    public string? Build { get; init; }

    /// <summary>
    /// Empty or missing folder that holds the root, the journals, the archives and the logs. Null for the folder named
    /// after the instance in the servers directory of the daemon, which must then be missing or empty.
    /// </summary>
    public string? ServerDirectory { get; init; }

    public bool Ssl { get; init; }

    public bool AutoStart { get; init; } = true;

    /// <summary>
    /// Whether to restore the license file of the point. A license is bound to the address of a machine.
    /// </summary>
    public bool IncludeLicense { get; init; } = true;

    /// <summary>
    /// A super user of the restored server, with its password as it was at the time of the point. The daemon stores
    /// it, and uses it to relocate depots and verify the archives.
    /// </summary>
    [Required]
    public required SuperUserRequest SuperUser { get; init; }

    /// <summary>
    /// The point to restore.
    /// </summary>
    [Required]
    public required BackupPointReference Source { get; init; }

    public override string ToString()
    {
        return $"RestoreInstanceRequest {{ Name = {Name}, Port = {Port}, Release = {Release}, Build = {Build}, "
            + $"SuperUser = {SuperUser.Name}, Source = {Source.SnapshotKey} }}";
    }
}

/// <summary>
/// Replaces the data of an instance with a backup point. The previous data is kept aside until the restore succeeds,
/// and put back if it fails.
/// </summary>
public sealed record RestoreBackupRequest
{
    /// <summary>
    /// The destination that holds the point.
    /// </summary>
    public Guid DestinationId { get; init; }

    /// <summary>
    /// Key of the manifest of the point, from <see cref="BackupPointDto.Key"/> or
    /// <see cref="BackupRunDestinationDto.SnapshotKey"/>.
    /// </summary>
    [Required]
    public required string SnapshotKey { get; init; }
}
