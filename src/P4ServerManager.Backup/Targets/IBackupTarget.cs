namespace P4ServerManager.Backup.Targets;

/// <summary>
/// A place that stores opaque objects by key: a folder, a bucket, a container, an FTP folder. Keys use <c>/</c> as
/// separator and follow <see cref="BackupObjectKey"/>. A target never interprets what it stores.
/// </summary>
public interface IBackupTarget : IAsyncDisposable
{
    TargetCapabilities Capabilities { get; }

    /// <summary>
    /// Where the target stores its objects, for the logs and the clients, such as <c>s3://bucket/prefix</c>. Never a
    /// credential.
    /// </summary>
    string Location { get; }

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="key"/>, replacing an existing object unless
    /// <see cref="BackupPutOptions.IfNotExists"/> is set.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="content">
    /// The content, seekable and at its start, so that the target can retry an upload or read its length.
    /// </param>
    /// <param name="options">Conditions of the write.</param>
    /// <param name="cancellationToken">Aborts the upload.</param>
    /// <exception cref="BackupObjectExistsException">
    /// <see cref="BackupPutOptions.IfNotExists"/> is set and the key exists.
    /// </exception>
    Task PutAsync(string key, Stream content, BackupPutOptions options, CancellationToken cancellationToken);

    /// <exception cref="BackupObjectNotFoundException">No object has this key.</exception>
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// The objects whose key starts with <paramref name="prefix"/>, in no particular order.
    /// </summary>
    IAsyncEnumerable<BackupObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an object. Deleting a key that does not exist is not an error.
    /// </summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// What a storage target guarantees beyond storing objects. A backup repository adapts to them, and the contract tests
/// check only what a target declares.
/// </summary>
[Flags]
public enum TargetCapabilities
{
    None = 0,

    /// <summary>
    /// <see cref="BackupPutOptions.IfNotExists"/> is enforced by the storage itself, atomically.
    /// </summary>
    ConditionalWrite = 1,

    /// <summary>
    /// <see cref="BackupPutOptions.Sha256"/> is checked by the storage, which refuses an object that does not match.
    /// </summary>
    ServerSideChecksum = 2,

    /// <summary>
    /// An object appears complete or not at all, even when an upload is cut short.
    /// </summary>
    AtomicWrite = 4,

    /// <summary>
    /// The storage can keep objects from being deleted or replaced for a while (Object Lock, immutability policy,
    /// retention policy).
    /// </summary>
    Immutability = 8,
}

/// <param name="IfNotExists">
/// Whether the write fails with a <see cref="BackupObjectExistsException"/> when the key already exists.
/// </param>
/// <param name="Sha256">SHA-256 of the content, for a target that checks it.</param>
public sealed record BackupPutOptions(bool IfNotExists = false, byte[]? Sha256 = null);

/// <param name="Key">Key of the object, relative to the root of the target.</param>
/// <param name="Size">Size of the object in bytes.</param>
public sealed record BackupObjectInfo(string Key, long Size);
