using System.Security.Cryptography;

using Microsoft.AspNetCore.DataProtection;

namespace P4ServerManager.Daemon.Services.Instances.Admin;

/// <summary>
/// Encrypts the passwords of the super users before they go to the database, with ASP.NET Core Data Protection. Its
/// keys are in <see cref="DaemonPaths.KeysDirectory"/>.
/// </summary>
public sealed class SuperUserPasswordProtector(IDataProtectionProvider dataProtectionProvider)
{
    private const string Purpose = "P4ServerManager.Instances.SuperUserPassword.v1";

    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(Purpose);

    public string Protect(string password)
    {
        return protector.Protect(password);
    }

    /// <exception cref="CryptographicException">
    /// The keys that encrypted the password are missing, for example after a restore without them.
    /// </exception>
    public string Unprotect(string protectedPassword)
    {
        return protector.Unprotect(protectedPassword);
    }
}
