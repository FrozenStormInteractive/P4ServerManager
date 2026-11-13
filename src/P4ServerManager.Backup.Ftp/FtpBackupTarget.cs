using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;

using FluentFTP;
using FluentFTP.Client.BaseClient;
using FluentFTP.Exceptions;

using P4ServerManager.Backup.Targets;

namespace P4ServerManager.Backup.Ftp;

/// <summary>
/// A folder of an FTP server, over TLS unless asked otherwise. An object is uploaded under a temporary name, then
/// renamed: it appears whole or not at all. FTP has no conditional write: the existence of an object is checked right
/// before the rename. One connection serves the target, one command at a time.
/// </summary>
public sealed class FtpBackupTarget : IBackupTarget
{
    private const int BufferSize = 81920;
    
    public TargetCapabilities Capabilities => TargetCapabilities.AtomicWrite;

    public string Location => $"ftp://{options.Host}{(options.Port is { } port ? $":{port}" : string.Empty)}{root}";

    private readonly AsyncFtpClient client;
    private readonly FtpTargetOptions options;
    private readonly string root;
    private readonly SemaphoreSlim connection = new(1, 1);

    public FtpBackupTarget(FtpTargetOptions options)
    {
        this.options = options;
        root = "/" + options.RootPath.Trim('/');
        if (root.Length > 1)
        {
            BackupObjectKey.Validate(root[1..]);
        }

        int port = options.Port ?? (options.Encryption == FtpTargetEncryption.Implicit ? 990 : 21);
        client = new AsyncFtpClient(options.Host, options.UserName, options.Password, port);
        client.Config.EncryptionMode = options.Encryption switch
        {
            FtpTargetEncryption.Explicit => FtpEncryptionMode.Explicit,
            FtpTargetEncryption.Implicit => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.None,
        };
        client.Config.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        client.Config.DataConnectionEncryption = options.Encryption != FtpTargetEncryption.None;
        client.Config.ValidateAnyCertificate = false;
        client.ValidateCertificate += ValidateCertificate;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        return await RunAsync(token => client.FileExists(GetPath(key), token), cancellationToken);
    }

    public async Task PutAsync(
        string key,
        Stream content,
        BackupPutOptions putOptions,
        CancellationToken cancellationToken)
    {
        string remote = GetPath(key);
        string directory = remote[..remote.LastIndexOf('/')];
        string temporary = $"{directory}/.{remote[(remote.LastIndexOf('/') + 1)..]}.{Guid.NewGuid():N}.partial";
        await RunAsync(
            async token =>
            {
                FtpStatus status = await client.UploadStream(
                    content,
                    temporary,
                    FtpRemoteExists.Overwrite,
                    createRemoteDir: true,
                    token: token);
                if (status != FtpStatus.Success)
                {
                    throw new BackupTargetException($"The FTP server refused the upload of {temporary}.");
                }

                bool exists = await client.FileExists(remote, token);
                if (exists && putOptions.IfNotExists)
                {
                    await client.DeleteFile(temporary, token);
                    throw new BackupObjectExistsException(key);
                }
                
                if (exists)
                {
                    await client.DeleteFile(remote, token);
                }
                
                await client.Rename(temporary, remote, token);
                return true;
            },
            cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        string remote = GetPath(key);
        Directory.CreateDirectory(options.TemporaryDirectory);
        FileStream file = new(
            Path.Combine(options.TemporaryDirectory, $"{Guid.NewGuid():N}.download"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            bool found = await RunAsync(
                async token => await client.FileExists(remote, token)
                    && await client.DownloadStream(file, remote, token: token),
                cancellationToken);
            if (!found)
            {
                throw new BackupObjectNotFoundException(key);
            }
            
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    public async IAsyncEnumerable<BackupObjectInfo> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        BackupObjectKey.ValidatePrefix(prefix);
        int separator = prefix.LastIndexOf('/');
        string directory = separator < 0 ? root : $"{root}/{prefix[..separator]}".Replace("//", "/");
        FtpListItem[] items = await RunAsync(
            async token => await client.DirectoryExists(directory, token)
                ? await client.GetListing(directory, FtpListOption.Recursive, token)
                : [],
            cancellationToken);

        string rootPrefix = root == "/" ? "/" : root + "/";
        foreach (FtpListItem item in items)
        {
            if (item.Type != FtpObjectType.File || !item.FullName.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            
            string key = item.FullName[rootPrefix.Length..];
            if (key.StartsWith(prefix, StringComparison.Ordinal) && BackupObjectKey.IsValid(key))
            {
                yield return new BackupObjectInfo(key, item.Size);
            }
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        string remote = GetPath(key);
        await RunAsync(
            async token =>
            {
                if (await client.FileExists(remote, token))
                {
                    await client.DeleteFile(remote, token);
                }
                return true;
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected)
            {
                await client.Disconnect();
            }
        }
        catch (Exception exception) when (exception is IOException or FtpException)
        {
            // The server closes the connection by itself.
        }
        await client.DisposeAsync();
        connection.Dispose();
    }

    private string GetPath(string key)
    {
        BackupObjectKey.Validate(key);
        return root == "/" ? "/" + key : $"{root}/{key}";
    }

    /// <summary>
    /// Runs commands on the connection, one caller at a time, connecting first if needed.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await connection.WaitAsync(cancellationToken);
        try
        {
            if (!client.IsConnected)
            {
                await client.Connect(cancellationToken);
            }
            return await action(cancellationToken);
        }
        catch (Exception exception) when (exception is FtpException or IOException or AuthenticationException)
        {
            throw new BackupTargetException($"FTP server {options.Host}: {exception.Message}", exception);
        }
        finally
        {
            connection.Release();
        }
    }

    private void ValidateCertificate(BaseFtpClient control, FtpSslValidationEventArgs e)
    {
        if (e.PolicyErrors == SslPolicyErrors.None)
        {
            e.Accept = true;
            return;
        }
        if (options.CertificateFingerprint is { Length: > 0 } expected && e.Certificate is not null)
        {
            string actual = Convert.ToHexString(SHA256.HashData(e.Certificate.GetRawCertData()));
            e.Accept = string.Equals(
                actual,
                expected.Replace(":", string.Empty).Replace(" ", string.Empty),
                StringComparison.OrdinalIgnoreCase);
            return;
        }
        e.Accept = false;
    }
}

/// <summary>
/// How an FTP target protects the connection.
/// </summary>
public enum FtpTargetEncryption
{
    Explicit,
    Implicit,
    None,
}

/// <param name="Host">Host name or address of the server.</param>
/// <param name="Port">Port, or null for the default of <paramref name="Encryption"/>.</param>
/// <param name="Encryption">How the connection is protected.</param>
/// <param name="RootPath">Folder of the objects on the server.</param>
/// <param name="UserName">User to log in as.</param>
/// <param name="Password">Its password.</param>
/// <param name="CertificateFingerprint">
/// SHA-256 fingerprint of the certificate of the server, in hexadecimal, to trust a self-signed certificate. Null to
/// validate the certificate as usual.
/// </param>
/// <param name="TemporaryDirectory">Where an object is downloaded before it is read.</param>
public sealed record FtpTargetOptions(
    string Host,
    int? Port,
    FtpTargetEncryption Encryption,
    string RootPath,
    string UserName,
    string Password,
    string? CertificateFingerprint,
    string TemporaryDirectory);
