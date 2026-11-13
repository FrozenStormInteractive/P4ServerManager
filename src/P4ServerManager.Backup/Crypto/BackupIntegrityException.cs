namespace P4ServerManager.Backup.Crypto;

/// <summary>
/// A stored object or manifest that is not what was written: corrupted, cut short, or encrypted with another key.
/// </summary>
public sealed class BackupIntegrityException(string message, Exception? innerException = null)
    : IOException(message, innerException);
