using System.Net;
using System.Runtime.CompilerServices;

using Google;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

using P4ServerManager.Backup.Targets;

using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace P4ServerManager.Backup.Gcs;

/// <summary>
/// A bucket of Google Cloud Storage. A large object goes up with a resumable upload, which creates it only once
/// complete. Google enforces the conditional writes through the generation of the object.
/// </summary>
public sealed class GcsBackupTarget : IBackupTarget
{
    private const int BufferSize = 81920;
    
    public TargetCapabilities Capabilities => TargetCapabilities.ConditionalWrite | TargetCapabilities.AtomicWrite;

    public string Location => $"gs://{options.Bucket}/{path.Prefix}";

    private readonly StorageClient client;
    private readonly GcsTargetOptions options;
    private readonly BackupTargetPath path;
    
    public GcsBackupTarget(GcsTargetOptions options)
    {
        this.options = options;
        path = new BackupTargetPath(options.Prefix);
        StorageClientBuilder builder = new();
        if (options.BaseUri is not null)
        {
            builder.BaseUri = options.BaseUri;
        }

        if (options.ServiceAccountJson is not null)
        {
            builder.Credential = CredentialFactory
                .FromJson<ServiceAccountCredential>(options.ServiceAccountJson)
                .ToGoogleCredential();
        }
        else if (options.BaseUri is not null)
        {
            // An emulator takes no credentials. Google Cloud Storage itself always needs them.
            builder.UnauthenticatedAccess = true;
        }
        else
        {
            throw new ArgumentException("A Google Cloud Storage target needs the key of a service account.");
        }
        client = builder.Build();
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetObjectAsync(options.Bucket, path.ToRemote(key), cancellationToken: cancellationToken);
            return true;
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task PutAsync(
        string key,
        Stream content,
        BackupPutOptions putOptions,
        CancellationToken cancellationToken)
    {
        UploadObjectOptions upload = new();
        if (putOptions.IfNotExists)
        {
            // Generation 0: no live object has this name.
            upload.IfGenerationMatch = 0;
        }

        try
        {
            await client.UploadObjectAsync(
                options.Bucket,
                path.ToRemote(key),
                "application/octet-stream",
                content,
                upload,
                cancellationToken);
        }
        catch (GoogleApiException exception) when (putOptions.IfNotExists
                                                   && exception.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new BackupObjectExistsException(key);
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        // The client only downloads into a stream: the object is read from a temporary file, deleted when closed.
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
            await client.DownloadObjectAsync(
                options.Bucket,
                path.ToRemote(key),
                file,
                cancellationToken: cancellationToken);
            file.Position = 0;
            return file;
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            await file.DisposeAsync();
            throw new BackupObjectNotFoundException(key);
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
        await foreach (StorageObject item in client.ListObjectsAsync(options.Bucket, path.ToRemotePrefix(prefix))
            .WithCancellation(cancellationToken))
        {
            if (path.ToKey(item.Name) is { } key)
            {
                yield return new BackupObjectInfo(key, (long)(item.Size ?? 0));
            }
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteObjectAsync(options.Bucket, path.ToRemote(key), cancellationToken: cancellationToken);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Nothing to delete.
        }
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <param name="Bucket">The bucket.</param>
/// <param name="Prefix">Folder of the objects in the bucket, if any.</param>
/// <param name="ServiceAccountJson">
/// Key of a service account, as the JSON file Google gives. Required for Google Cloud Storage; null with an emulator
/// that takes no credentials, which is then reached without any.
/// </param>
/// <param name="TemporaryDirectory">Where an object is downloaded before it is read.</param>
/// <param name="BaseUri">
/// URL of the JSON API of an emulator, such as <c>http://localhost:4443/storage/v1/</c>. Null for Google Cloud Storage.
/// </param>
public sealed record GcsTargetOptions(
    string Bucket,
    string? Prefix,
    string? ServiceAccountJson,
    string TemporaryDirectory,
    string? BaseUri = null);
