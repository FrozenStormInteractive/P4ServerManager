namespace P4ServerManager.Backup.Repository;

/// <summary>
/// A repository failed to open: no repository, an unknown format, or a wrong passphrase.
/// </summary>
public class BackupRepositoryException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// The passphrase does not open the repository.
/// </summary>
public sealed class BackupRepositoryPassphraseException(Exception? innerException = null)
    : BackupRepositoryException("The passphrase does not open the repository.", innerException);
