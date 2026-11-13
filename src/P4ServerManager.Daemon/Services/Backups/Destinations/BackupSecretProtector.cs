using Microsoft.AspNetCore.DataProtection;

namespace P4ServerManager.Daemon.Services.Backups.Destinations;

/// <summary>
/// Encrypts the passphrases and the credentials of the backup destinations before they go to the database, with
/// ASP.NET Core Data Protection, whose keys are in <see cref="DaemonPaths.KeysDirectory"/>. The passphrase is the only
/// way to read the backups without these keys.
/// </summary>
public sealed class BackupSecretProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector passphrases =
        dataProtectionProvider.CreateProtector("P4ServerManager.Backups.Passphrase.v1");

    private readonly IDataProtector credentials =
        dataProtectionProvider.CreateProtector("P4ServerManager.Backups.Credentials.v1");

    public string ProtectPassphrase(string passphrase)
    {
        return passphrases.Protect(passphrase);
    }

    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The keys that encrypted the passphrase are missing.
    /// </exception>
    public string UnprotectPassphrase(string protectedPassphrase)
    {
        return passphrases.Unprotect(protectedPassphrase);
    }

    public string ProtectCredentials(string json)
    {
        return credentials.Protect(json);
    }

    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The keys that encrypted the credentials are missing.
    /// </exception>
    public string UnprotectCredentials(string protectedJson)
    {
        return credentials.Unprotect(protectedJson);
    }
}
