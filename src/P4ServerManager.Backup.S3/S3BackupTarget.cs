using System.Net;
using System.Runtime.CompilerServices;

using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using P4ServerManager.Backup.Targets;

namespace P4ServerManager.Backup.S3;

/// <summary>
/// A bucket of Amazon S3 or of a storage with an S3-compatible API. An object larger than
/// <see cref="MultipartThreshold"/> goes up in parts, which the storage assembles only once all are there. Amazon S3
/// enforces the conditional writes and checks the SHA-256 of the simple uploads; an S3-compatible storage is not assumed
/// to, so the repository checks before it writes there.
/// </summary>
public sealed class S3BackupTarget : IBackupTarget
{
    /// <summary>
    /// Above this size an object goes up in parts.
    /// </summary>
    public const long MultipartThreshold = 64L * 1024 * 1024;

    private const long MinimumPartSize = 16L * 1024 * 1024;

    /// <summary>
    /// S3 accepts up to 10,000 parts: the parts grow with the object.
    /// </summary>
    private const int MaximumParts = 9000;
    
    public TargetCapabilities Capabilities => options.IsAmazonS3
        ? TargetCapabilities.ConditionalWrite | TargetCapabilities.ServerSideChecksum | TargetCapabilities.AtomicWrite
        : TargetCapabilities.AtomicWrite;

    public string Location => options.IsAmazonS3
        ? $"s3://{options.Bucket}/{path.Prefix}"
        : $"{options.ServiceUrl!.TrimEnd('/')}/{options.Bucket}/{path.Prefix}";

    private readonly AmazonS3Client client;
    private readonly S3TargetOptions options;
    private readonly BackupTargetPath path;

    public S3BackupTarget(S3TargetOptions options)
    {
        this.options = options;
        path = new BackupTargetPath(options.Prefix);

        AmazonS3Config config = new() { ForcePathStyle = options.ForcePathStyle };
        if (options.IsAmazonS3)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
        else
        {
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
            // Many S3-compatible storages refuse the checksums the SDK adds by default.
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), config);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetObjectMetadataAsync(options.Bucket, path.ToRemote(key), cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
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
        string remote = path.ToRemote(key);
        bool conditional = putOptions.IfNotExists && Capabilities.HasFlag(TargetCapabilities.ConditionalWrite);
        try
        {
            if (content.Length > MultipartThreshold)
            {
                await PutInPartsAsync(remote, content, conditional, cancellationToken);
                return;
            }

            PutObjectRequest request = new()
            {
                BucketName = this.options.Bucket,
                Key = remote,
                InputStream = content,
                AutoCloseStream = false,
                AutoResetStreamPosition = true,
            };
            if (conditional)
            {
                request.IfNoneMatch = "*";
            }
            if (putOptions.Sha256 is not null && Capabilities.HasFlag(TargetCapabilities.ServerSideChecksum))
            {
                request.ChecksumSHA256 = Convert.ToBase64String(putOptions.Sha256);
            }
            if (!string.IsNullOrEmpty(this.options.StorageClass))
            {
                request.StorageClass = new S3StorageClass(this.options.StorageClass);
            }
            await client.PutObjectAsync(request, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (conditional && IsConditionFailure(exception))
        {
            throw new BackupObjectExistsException(key);
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            GetObjectResponse response = await client.GetObjectAsync(options.Bucket, path.ToRemote(key), cancellationToken);
            return new ResponseStream(response);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new BackupObjectNotFoundException(key);
        }
    }

    public async IAsyncEnumerable<BackupObjectInfo> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ListObjectsV2Request request = new()
        {
            BucketName = options.Bucket,
            Prefix = path.ToRemotePrefix(prefix),
        };
        do
        {
            ListObjectsV2Response response = await client.ListObjectsV2Async(request, cancellationToken);
            foreach (S3Object item in response.S3Objects ?? [])
            {
                if (path.ToKey(item.Key) is { } key)
                {
                    yield return new BackupObjectInfo(key, item.Size ?? 0);
                }
            }
            request.ContinuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (request.ContinuationToken is not null);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await client.DeleteObjectAsync(options.Bucket, path.ToRemote(key), cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task PutInPartsAsync(string remote, Stream content, bool conditional, CancellationToken cancellationToken)
    {
        long length = content.Length;
        long partSize = Math.Max(MinimumPartSize, (length + MaximumParts - 1) / MaximumParts);
        InitiateMultipartUploadRequest initiate = new() { BucketName = options.Bucket, Key = remote };
        if (!string.IsNullOrEmpty(options.StorageClass))
        {
            initiate.StorageClass = new S3StorageClass(options.StorageClass);
        }
        InitiateMultipartUploadResponse upload = await client.InitiateMultipartUploadAsync(initiate, cancellationToken);
        try
        {
            List<PartETag> parts = [];
            int number = 1;
            for (long offset = 0; offset < length; offset += partSize, number++)
            {
                long size = Math.Min(partSize, length - offset);
                await using ReadOnlySubStream part = new(content, offset, size);
                UploadPartResponse response = await client.UploadPartAsync(
                    new UploadPartRequest
                    {
                        BucketName = options.Bucket,
                        Key = remote,
                        UploadId = upload.UploadId,
                        PartNumber = number,
                        PartSize = size,
                        InputStream = part,
                    },
                    cancellationToken);
                parts.Add(new PartETag(number, response.ETag));
            }

            CompleteMultipartUploadRequest complete = new()
            {
                BucketName = options.Bucket,
                Key = remote,
                UploadId = upload.UploadId,
                PartETags = parts,
            };
            if (conditional)
            {
                complete.IfNoneMatch = "*";
            }
            
            await client.CompleteMultipartUploadAsync(complete, cancellationToken);
        }
        catch
        {
            try
            {
                await client.AbortMultipartUploadAsync(options.Bucket, remote, upload.UploadId, CancellationToken.None);
            }
            catch (AmazonServiceException)
            {
                // A lifecycle rule of the bucket removes the parts of an abandoned upload.
            }
            throw;
        }
    }

    private static bool IsConditionFailure(AmazonS3Exception exception)
    {
        return exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict;
    }

    /// <summary>
    /// The body of a response, which disposes of the response with it.
    /// </summary>
    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream inner = response.ResponseStream;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            return inner.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <param name="Bucket">The bucket.</param>
/// <param name="Prefix">Folder of the objects in the bucket, if any.</param>
/// <param name="Region">Region of the bucket.</param>
/// <param name="ServiceUrl">URL of an S3-compatible API, or null for Amazon S3.</param>
/// <param name="ForcePathStyle">Whether the bucket goes in the path of the URLs.</param>
/// <param name="AccessKeyId">Identifier of the access key.</param>
/// <param name="SecretAccessKey">Secret of the access key.</param>
/// <param name="StorageClass">Storage class of the objects, or null for the default.</param>
public sealed record S3TargetOptions(
    string Bucket,
    string? Prefix,
    string Region,
    string? ServiceUrl,
    bool ForcePathStyle,
    string AccessKeyId,
    string SecretAccessKey,
    string? StorageClass)
{
    /// <summary>
    /// Whether the storage is Amazon S3 itself, whose guarantees are known.
    /// </summary>
    public bool IsAmazonS3 => string.IsNullOrEmpty(ServiceUrl);
}
