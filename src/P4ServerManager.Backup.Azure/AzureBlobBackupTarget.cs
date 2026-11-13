using System.Net;
using System.Runtime.CompilerServices;

using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

using P4ServerManager.Backup.Targets;

namespace P4ServerManager.Backup.Azure;

/// <summary>
/// A container of Azure Blob Storage. A large object goes up in blocks, committed together: it appears whole or not at
/// all. Azure enforces the conditional writes.
/// </summary>
public sealed class AzureBlobBackupTarget : IBackupTarget
{
    public TargetCapabilities Capabilities => TargetCapabilities.ConditionalWrite | TargetCapabilities.AtomicWrite;

    public string Location { get; }
    
    private readonly BlobContainerClient container;
    private readonly BackupTargetPath path;

    public AzureBlobBackupTarget(AzureBlobTargetOptions options)
    {
        path = new BackupTargetPath(options.Prefix);
        Uri containerUri = new($"{options.AccountUrl.TrimEnd('/')}/{options.Container}");
        if (!string.IsNullOrEmpty(options.AccountKey))
        {
            container = new BlobContainerClient(
                containerUri,
                new StorageSharedKeyCredential(GetAccountName(containerUri), options.AccountKey));
        }
        else if (!string.IsNullOrEmpty(options.SasToken))
        {
            container = new BlobContainerClient(containerUri, new AzureSasCredential(options.SasToken.TrimStart('?')));
        }
        else
        {
            throw new ArgumentException("An Azure Blob Storage target needs the key of its account or a shared access signature.");
        }
        Location = $"{containerUri}/{path.Prefix}";
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        Response<bool> exists = await container.GetBlobClient(path.ToRemote(key)).ExistsAsync(cancellationToken);
        return exists.Value;
    }

    public async Task PutAsync(
        string key,
        Stream content,
        BackupPutOptions options,
        CancellationToken cancellationToken)
    {
        BlobUploadOptions upload = new()
        {
            TransferOptions = new StorageTransferOptions { MaximumConcurrency = 4 },
        };
        if (options.IfNotExists)
        {
            upload.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
        }

        try
        {
            await container.GetBlobClient(path.ToRemote(key)).UploadAsync(content, upload, cancellationToken);
        }
        catch (RequestFailedException exception) when (options.IfNotExists
            && exception.Status is (int)HttpStatusCode.Conflict or (int)HttpStatusCode.PreconditionFailed)
        {
            throw new BackupObjectExistsException(key);
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await container.GetBlobClient(path.ToRemote(key)).OpenReadAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
        {
            throw new BackupObjectNotFoundException(key);
        }
    }

    public async IAsyncEnumerable<BackupObjectInfo> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (BlobItem item in container.GetBlobsAsync(
            BlobTraits.None,
            BlobStates.None,
            path.ToRemotePrefix(prefix),
            cancellationToken))
        {
            if (path.ToKey(item.Name) is { } key)
            {
                yield return new BackupObjectInfo(key, item.Properties.ContentLength ?? 0);
            }
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await container.GetBlobClient(path.ToRemote(key)).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The account: the first label of the host, or the first segment of the path for an emulator such as Azurite
    /// (<c>http://127.0.0.1:10000/devstoreaccount1</c>).
    /// </summary>
    private static string GetAccountName(Uri containerUri)
    {
        return containerUri.IsLoopback || IPAddress.TryParse(containerUri.Host, out _)
            ? containerUri.Segments[1].TrimEnd('/')
            : containerUri.Host.Split('.')[0];
    }
}

/// <param name="AccountUrl">URL of the Blob service of the account.</param>
/// <param name="Container">The container.</param>
/// <param name="Prefix">Folder of the objects in the container, if any.</param>
/// <param name="AccountKey">Key of the account, or null with a shared access signature.</param>
/// <param name="SasToken">Shared access signature of the container, or null with the key of the account.</param>
public sealed record AzureBlobTargetOptions(
    string AccountUrl,
    string Container,
    string? Prefix,
    string? AccountKey,
    string? SasToken);
