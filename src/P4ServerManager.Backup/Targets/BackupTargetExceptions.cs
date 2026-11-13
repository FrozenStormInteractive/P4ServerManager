namespace P4ServerManager.Backup.Targets;

/// <summary>
/// A failure of a storage target that is not about one object: unreachable, refused credentials, missing bucket.
/// </summary>
public class BackupTargetException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// A conditional write found the key already there.
/// </summary>
public sealed class BackupObjectExistsException(string key)
    : BackupTargetException($"Object '{key}' already exists.")
{
    public string Key { get; } = key;
}

/// <summary>
/// No object has the key that was read.
/// </summary>
public sealed class BackupObjectNotFoundException(string key)
    : BackupTargetException($"Object '{key}' does not exist.")
{
    public string Key { get; } = key;
}
