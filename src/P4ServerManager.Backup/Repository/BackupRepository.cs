using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using P4ServerManager.Backup.Crypto;
using P4ServerManager.Backup.Manifests;
using P4ServerManager.Backup.Targets;

using ZstdSharp;

namespace P4ServerManager.Backup.Repository;

/// <summary>
/// A backup repository on a storage target: encrypted objects named after their content, and the manifests of the
/// backup points. Layout:
/// <code>
/// config                                  how to open the repository with its passphrase, in clear
/// objects/&lt;aa&gt;/&lt;bb&gt;/&lt;name&gt;                contents, compressed when useful, encrypted
/// snapshots/&lt;source&gt;/&lt;time&gt;-&lt;id&gt;          manifests of the points, compressed, encrypted
/// </code>
/// </summary>
public sealed class BackupRepository : IDisposable
{
    public const string ConfigKey = "config";

    public const string ObjectsPrefix = "objects/";

    public const string SnapshotsPrefix = "snapshots/";

    /// <summary>
    /// Suffix of the file a restore writes before it is checked and renamed.
    /// </summary>
    public const string RestoreSuffix = ".p4sm-restore";

    /// <summary>
    /// Problems a verification keeps, so that a damaged repository does not flood its report.
    /// </summary>
    public const int MaxProblems = 500;

    /// <summary>
    /// Up to this many objects, a quick verification asks for each one; above, it lists the objects once.
    /// </summary>
    private const int ExistsCheckLimit = 200;

    private const int CompressionLevel = 3;
    private const int BufferSize = 81920;
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    
    public IBackupTarget Target { get; }

    public Guid RepositoryId { get; }

    private readonly RepositoryKeys keys;
    private readonly BackupRepositoryOptions options;

    private BackupRepository(IBackupTarget target, Guid repositoryId, RepositoryKeys keys, BackupRepositoryOptions options)
    {
        Target = target;
        RepositoryId = repositoryId;
        this.keys = keys;
        this.options = options;
    }

    /// <summary>
    /// Whether the target holds a repository.
    /// </summary>
    public static Task<bool> ExistsAsync(IBackupTarget target, CancellationToken cancellationToken)
    {
        return target.ExistsAsync(ConfigKey, cancellationToken);
    }

    /// <summary>
    /// Creates a repository on an empty target, with a new random master key that <paramref name="passphrase"/>
    /// unlocks.
    /// </summary>
    /// <exception cref="BackupRepositoryException">The target already holds a repository.</exception>
    public static async Task<BackupRepository> InitializeAsync(
        IBackupTarget target,
        string passphrase,
        BackupRepositoryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        Guid repositoryId = Guid.NewGuid();
        byte[] masterKey = RandomNumberGenerator.GetBytes(RepositoryKeys.KeySize);
        try
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(ObjectFormat.NonceSize);
            byte[] ciphertext = new byte[RepositoryKeys.KeySize + ObjectFormat.TagSize];
            byte[] wrappingKey = DeriveWrappingKey(passphrase, salt, options.KeyDerivationIterations);
            try
            {
                using AesGcm aes = new(wrappingKey, ObjectFormat.TagSize);
                aes.Encrypt(
                    nonce,
                    masterKey,
                    ciphertext.AsSpan(0, RepositoryKeys.KeySize),
                    ciphertext.AsSpan(RepositoryKeys.KeySize),
                    repositoryId.ToByteArray());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
            }

            RepositoryConfig config = new()
            {
                Format = RepositoryConfig.FormatName,
                Version = RepositoryConfig.CurrentVersion,
                RepositoryId = repositoryId,
                CreatedAt = DateTimeOffset.UtcNow,
                Kdf = new RepositoryKeyDerivation
                {
                    Algorithm = RepositoryKeyDerivation.Pbkdf2Sha256,
                    Iterations = options.KeyDerivationIterations,
                    Salt = salt,
                },
                Key = new RepositoryWrappedKey
                {
                    Algorithm = RepositoryWrappedKey.Aes256Gcm,
                    Nonce = nonce,
                    Ciphertext = ciphertext,
                },
            };

            using MemoryStream content = new(
                JsonSerializer.SerializeToUtf8Bytes(config, RepositoryJsonContext.Default.RepositoryConfig));
            if (!await PutIfAbsentAsync(target, ConfigKey, content, null, cancellationToken))
            {
                throw new BackupRepositoryException($"{target.Location} already holds a backup repository.");
            }

            return new BackupRepository(target, repositoryId, RepositoryKeys.Derive(masterKey, repositoryId), options);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>
    /// Opens the repository of a target with its passphrase.
    /// </summary>
    /// <exception cref="BackupRepositoryPassphraseException">The passphrase is wrong.</exception>
    /// <exception cref="BackupRepositoryException">
    /// The target holds no repository, or one in a format this version does not know.
    /// </exception>
    public static async Task<BackupRepository> OpenAsync(
        IBackupTarget target,
        string passphrase,
        BackupRepositoryOptions options,
        CancellationToken cancellationToken)
    {
        RepositoryConfig config = await ReadConfigAsync(target, cancellationToken);
        if (config.Kdf.Algorithm != RepositoryKeyDerivation.Pbkdf2Sha256
            || config.Key.Algorithm != RepositoryWrappedKey.Aes256Gcm
            || config.Key.Nonce.Length != ObjectFormat.NonceSize
            || config.Key.Ciphertext.Length != RepositoryKeys.KeySize + ObjectFormat.TagSize
            || config.Kdf.Iterations <= 0)
        {
            throw new BackupRepositoryException($"The repository of {target.Location} uses unknown keys.");
        }

        byte[] masterKey = new byte[RepositoryKeys.KeySize];
        byte[] wrappingKey = DeriveWrappingKey(passphrase, config.Kdf.Salt, config.Kdf.Iterations);
        try
        {
            using AesGcm aes = new(wrappingKey, ObjectFormat.TagSize);
            aes.Decrypt(
                config.Key.Nonce,
                config.Key.Ciphertext.AsSpan(0, RepositoryKeys.KeySize),
                config.Key.Ciphertext.AsSpan(RepositoryKeys.KeySize),
                masterKey,
                config.RepositoryId.ToByteArray());
            return new BackupRepository(
                target,
                config.RepositoryId,
                RepositoryKeys.Derive(masterKey, config.RepositoryId),
                options);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new BackupRepositoryPassphraseException(exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>
    /// Name of the object that holds a content, from its SHA-256.
    /// </summary>
    public string GetObjectName(string sha256)
    {
        return keys.GetObjectName(Convert.FromHexString(sha256));
    }

    public static string GetObjectKey(string name)
    {
        return $"{ObjectsPrefix}{name[..2]}/{name[2..4]}/{name}";
    }

    public Task<bool> ObjectExistsAsync(string name, CancellationToken cancellationToken)
    {
        return Target.ExistsAsync(GetObjectKey(name), cancellationToken);
    }

    /// <summary>
    /// Stores the content of a file, unless the repository already has it. The file is read once: what it holds at that
    /// moment is what is stored, and what the result tells.
    /// </summary>
    /// <param name="path">The file. It may be written meanwhile: it is opened with every share mode.</param>
    /// <param name="compress">Whether to compress the content: false for a content already compressed.</param>
    /// <param name="cancellationToken">Aborts the read and the upload.</param>
    public async Task<StoredObject> PutFileAsync(string path, bool compress, CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await PutContentAsync(
            (destination, token) => source.CopyToAsync(destination, BufferSize, token),
            compress,
            cancellationToken);
    }

    /// <summary>
    /// Stores the content <paramref name="write"/> writes, unless the repository already has it.
    /// </summary>
    public async Task<StoredObject> PutContentAsync(
        Func<Stream, CancellationToken, Task> write,
        bool compress,
        CancellationToken cancellationToken)
    {
        string temporaryPath = CreateTemporaryPath();
        try
        {
            EncodedObject encoded = await EncodeAsync(temporaryPath, write, compress, cancellationToken);
            string name = keys.GetObjectName(encoded.ContentSha256);
            await using FileStream content = OpenTemporaryFile(temporaryPath);
            bool uploaded = await PutIfAbsentAsync(
                Target,
                GetObjectKey(name),
                content,
                encoded.StoredSha256,
                cancellationToken);
            return new StoredObject(
                name,
                Convert.ToHexStringLower(encoded.ContentSha256),
                encoded.ContentLength,
                encoded.StoredLength,
                uploaded);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    /// <summary>
    /// Opens an object to read its content.
    /// </summary>
    /// <exception cref="BackupObjectNotFoundException">The repository has no such object.</exception>
    /// <exception cref="BackupIntegrityException">The object is not readable with the keys of the repository.</exception>
    public async Task<Stream> OpenObjectAsync(string name, CancellationToken cancellationToken)
    {
        return await OpenDecodedAsync(GetObjectKey(name), cancellationToken);
    }

    /// <summary>
    /// Writes the content of a file of a point to <paramref name="path"/>, with its last write time. The content goes to
    /// <c>&lt;path&gt;.p4sm-restore</c> first and replaces <paramref name="path"/> only once its size and SHA-256 match the
    /// manifest: a failed restore never leaves a wrong file at <paramref name="path"/>.
    /// </summary>
    /// <param name="entry">The file, from the manifest of the point.</param>
    /// <param name="path">Where to write it. Its folder must exist.</param>
    /// <param name="cancellationToken">Aborts the download.</param>
    /// <exception cref="BackupObjectNotFoundException">The repository has no object for the file.</exception>
    /// <exception cref="BackupIntegrityException">The content is not the one the manifest lists.</exception>
    public async Task RestoreFileAsync(BackupManifestEntry entry, string path, CancellationToken cancellationToken)
    {
        string temporaryPath = path + RestoreSuffix;
        try
        {
            await using (Stream content = await OpenObjectAsync(entry.Object, cancellationToken))
            await using (FileStream file = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous))
            {
                string? problem;
                try
                {
                    problem = await CopyContentAsync(content, file, entry, cancellationToken);
                }
                catch (ZstdException exception)
                {
                    throw new BackupIntegrityException($"The content of {entry.Path} cannot be decompressed.", exception);
                }
                if (problem is not null)
                {
                    throw new BackupIntegrityException($"{entry.Path}: {problem}");
                }
                await file.FlushAsync(cancellationToken);
            }

            File.SetLastWriteTimeUtc(temporaryPath, entry.ModifiedAt.UtcDateTime);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static string GetManifestKey(BackupManifestHeader header)
    {
        string timestamp = header.CreatedAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{SnapshotsPrefix}{header.SourceId:N}/{timestamp}-{header.Id:N}";
    }

    /// <summary>
    /// Writes the manifest of a backup point, which makes the point visible. Every object it lists must be stored first.
    /// </summary>
    /// <exception cref="BackupObjectExistsException">A point with the same identifier exists.</exception>
    public async Task<BackupSnapshotInfo> WriteManifestAsync(
        BackupManifestHeader header,
        IEnumerable<BackupManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        string key = GetManifestKey(header);
        string temporaryPath = CreateTemporaryPath();
        try
        {
            EncodedObject encoded = await EncodeAsync(
                temporaryPath,
                async (destination, token) =>
                {
                    await WriteLineAsync(
                        destination,
                        JsonSerializer.SerializeToUtf8Bytes(header, ManifestJsonContext.Default.BackupManifestHeader),
                        token);
                    foreach (BackupManifestEntry entry in entries)
                    {
                        await WriteLineAsync(
                            destination,
                            JsonSerializer.SerializeToUtf8Bytes(entry, ManifestJsonContext.Default.BackupManifestEntry),
                            token);
                    }
                },
                compress: true,
                cancellationToken);

            await using FileStream content = OpenTemporaryFile(temporaryPath);
            if (!await PutIfAbsentAsync(Target, key, content, encoded.StoredSha256, cancellationToken))
            {
                throw new BackupObjectExistsException(key);
            }
            return new BackupSnapshotInfo(key, header.SourceId, header.CreatedAt, header.Id);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    /// <exception cref="BackupObjectNotFoundException">No manifest has this key.</exception>
    /// <exception cref="BackupIntegrityException">The manifest cannot be read.</exception>
    public async Task<BackupManifest> ReadManifestAsync(string key, CancellationToken cancellationToken)
    {
        await using Stream content = await OpenDecodedAsync(key, cancellationToken);
        using StreamReader reader = new(content, Encoding.UTF8);
        BackupManifestHeader header = await ReadHeaderAsync(reader, key, cancellationToken);

        List<BackupManifestEntry> entries = [];
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            entries.Add(Deserialize(line, ManifestJsonContext.Default.BackupManifestEntry)
                ?? throw new BackupIntegrityException($"Manifest {key} has an empty entry."));
        }
        
        return new BackupManifest(header, entries);
    }

    /// <summary>
    /// Reads only the header of a manifest: what the point is, without its files. The rest of the manifest is not read,
    /// so damage past the header goes unnoticed here.
    /// </summary>
    /// <exception cref="BackupObjectNotFoundException">No manifest has this key.</exception>
    /// <exception cref="BackupIntegrityException">The header cannot be read.</exception>
    public async Task<BackupManifestHeader> ReadManifestHeaderAsync(string key, CancellationToken cancellationToken)
    {
        await using Stream content = await OpenDecodedAsync(key, cancellationToken);
        using StreamReader reader = new(content, Encoding.UTF8);
        return await ReadHeaderAsync(reader, key, cancellationToken);
    }

    /// <summary>
    /// The backup points of the repository, or of one source, oldest first.
    /// </summary>
    public async IAsyncEnumerable<BackupSnapshotInfo> ListSnapshotsAsync(
        Guid? sourceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string prefix = sourceId is { } id ? $"{SnapshotsPrefix}{id:N}/" : SnapshotsPrefix;
        List<BackupSnapshotInfo> snapshots = [];
        await foreach (BackupObjectInfo info in Target.ListAsync(prefix, cancellationToken))
        {
            if (TryParseManifestKey(info.Key, out BackupSnapshotInfo? snapshot))
            {
                snapshots.Add(snapshot);
            }
        }

        foreach (BackupSnapshotInfo snapshot in snapshots.OrderBy(s => s.CreatedAt).ThenBy(s => s.Key, StringComparer.Ordinal))
        {
            yield return snapshot;
        }
    }

    public static bool TryParseManifestKey(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BackupSnapshotInfo? snapshot)
    {
        snapshot = null;
        if (!key.StartsWith(SnapshotsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = key[SnapshotsPrefix.Length..].Split('/');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out Guid sourceId))
        {
            return false;
        }

        int dash = parts[1].LastIndexOf('-');
        if (dash < 0
            || !Guid.TryParseExact(parts[1][(dash + 1)..], "N", out Guid id)
            || !DateTime.TryParseExact(
                parts[1][..dash],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out DateTime createdAt))
        {
            return false;
        }

        snapshot = new BackupSnapshotInfo(key, sourceId, new DateTimeOffset(createdAt, TimeSpan.Zero), id);
        return true;
    }

    /// <summary>
    /// Checks that every object of a point is in the repository. A deep verification also reads each one, decrypts it
    /// and compares its SHA-256 and its size with the manifest.
    /// </summary>
    public async Task<BackupVerificationResult> VerifyAsync(
        BackupManifest manifest,
        bool deep,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Several files can share an object: each object is checked once.
        Dictionary<string, BackupManifestEntry> objects = [];
        foreach (BackupManifestEntry entry in manifest.Entries)
        {
            objects.TryAdd(entry.Object, entry);
        }

        List<BackupVerificationProblem> problems = [];
        int problemCount = 0;
        void Report(BackupManifestEntry entry, string message)
        {
            problemCount++;
            if (problems.Count < MaxProblems)
            {
                problems.Add(new BackupVerificationProblem(entry.Object, entry.Path, message));
            }
        }

        HashSet<string>? existing = null;
        if (!deep && objects.Count > ExistsCheckLimit)
        {
            existing = [];
            await foreach (BackupObjectInfo info in Target.ListAsync(ObjectsPrefix, cancellationToken))
            {
                existing.Add(info.Key);
            }
        }

        long completed = 0;
        foreach ((string name, BackupManifestEntry entry) in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = GetObjectKey(name);
            if (!deep)
            {
                bool found = existing?.Contains(key) ?? await Target.ExistsAsync(key, cancellationToken);
                if (!found)
                {
                    Report(entry, "The object is missing.");
                }
            }
            else
            {
                string? problem = await CheckContentAsync(entry, cancellationToken);
                if (problem is not null)
                {
                    Report(entry, problem);
                }
            }

            completed++;
            progress?.Report(new BackupProgress(completed, objects.Count));
        }

        return new BackupVerificationResult(objects.Count, problems, problemCount);
    }

    public void Dispose()
    {
        keys.Dispose();
    }

    private async Task<string?> CheckContentAsync(BackupManifestEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream content = await OpenObjectAsync(entry.Object, cancellationToken);
            return await CopyContentAsync(content, Stream.Null, entry, cancellationToken);
        }
        catch (BackupObjectNotFoundException)
        {
            return "The object is missing.";
        }
        catch (Exception exception) when (exception is BackupIntegrityException or ZstdException)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// Copies a content to <paramref name="destination"/> and compares it with what the manifest says of it.
    /// </summary>
    /// <returns>What is wrong with the content, or null when it is what the manifest says.</returns>
    private static async Task<string?> CopyContentAsync(
        Stream content,
        Stream destination,
        BackupManifestEntry entry,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long size = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            size += read;
        }

        string sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return "The content does not match its SHA-256.";
        }
        return size != entry.Size ? $"The content has {size} bytes, not {entry.Size}." : null;
    }

    private static async Task<BackupManifestHeader> ReadHeaderAsync(
        StreamReader reader,
        string key,
        CancellationToken cancellationToken)
    {
        string? headerLine = await reader.ReadLineAsync(cancellationToken);
        BackupManifestHeader header = Deserialize(headerLine, ManifestJsonContext.Default.BackupManifestHeader)
            ?? throw new BackupIntegrityException($"Manifest {key} has no header.");
        if (header.FormatVersion != BackupManifestHeader.CurrentFormatVersion)
        {
            throw new BackupIntegrityException(
                $"Manifest {key} has version {header.FormatVersion} of the format, not {BackupManifestHeader.CurrentFormatVersion}.");
        }
        return header;
    }

    private async Task<Stream> OpenDecodedAsync(string key, CancellationToken cancellationToken)
    {
        Stream stored = await Target.OpenReadAsync(key, cancellationToken);
        DecryptingReadStream decrypted = await DecryptingReadStream.OpenAsync(stored, keys, cancellationToken);
        return decrypted.Compressed ? new DecompressionStream(decrypted, leaveOpen: false) : decrypted;
    }

    /// <summary>
    /// Writes an object to a temporary file: content, then compression if asked, then encryption.
    /// </summary>
    private async Task<EncodedObject> EncodeAsync(
        string temporaryPath,
        Func<Stream, CancellationToken, Task> write,
        bool compress,
        CancellationToken cancellationToken)
    {
        await using FileStream file = new(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous);
        await using HashingWriteStream stored = new(file);
        await using EncryptingWriteStream encrypted = new(stored, keys, compress);

        byte[] contentSha256;
        long contentLength;
        if (compress)
        {
            await using CompressionStream compressed = new(encrypted, CompressionLevel, leaveOpen: true);
            await using HashingWriteStream content = new(compressed);
            await write(content, cancellationToken);
            contentSha256 = content.GetHash();
            contentLength = content.BytesWritten;
        }
        else
        {
            await using HashingWriteStream content = new(encrypted);
            await write(content, cancellationToken);
            contentSha256 = content.GetHash();
            contentLength = content.BytesWritten;
        }

        await encrypted.FinishAsync(cancellationToken);
        await file.FlushAsync(cancellationToken);
        return new EncodedObject(contentSha256, contentLength, stored.GetHash(), stored.BytesWritten);
    }

    /// <summary>
    /// Stores an object unless the key exists: atomically when the target can, otherwise after asking whether it
    /// exists.
    /// </summary>
    /// <returns>Whether the object was stored.</returns>
    private static async Task<bool> PutIfAbsentAsync(
        IBackupTarget target,
        string key,
        Stream content,
        byte[]? sha256,
        CancellationToken cancellationToken)
    {
        if (!target.Capabilities.HasFlag(TargetCapabilities.ConditionalWrite)
            && await target.ExistsAsync(key, cancellationToken))
        {
            return false;
        }

        try
        {
            await target.PutAsync(key, content, new BackupPutOptions(IfNotExists: true, sha256), cancellationToken);
            return true;
        }
        catch (BackupObjectExistsException)
        {
            return false;
        }
    }

    private static async Task<RepositoryConfig> ReadConfigAsync(IBackupTarget target, CancellationToken cancellationToken)
    {
        RepositoryConfig? config;
        try
        {
            await using Stream content = await target.OpenReadAsync(ConfigKey, cancellationToken);
            config = await JsonSerializer.DeserializeAsync(
                content,
                RepositoryJsonContext.Default.RepositoryConfig,
                cancellationToken);
        }
        catch (BackupObjectNotFoundException exception)
        {
            throw new BackupRepositoryException($"{target.Location} holds no backup repository.", exception);
        }
        catch (JsonException exception)
        {
            throw new BackupRepositoryException($"The configuration of the repository of {target.Location} cannot be read.", exception);
        }

        if (config is null || config.Format != RepositoryConfig.FormatName)
        {
            throw new BackupRepositoryException($"{target.Location} holds no backup repository.");
        }
        if (config.Version != RepositoryConfig.CurrentVersion)
        {
            throw new BackupRepositoryException(
                $"The repository of {target.Location} has version {config.Version} of the format, not {RepositoryConfig.CurrentVersion}.");
        }
        return config;
    }

    private static byte[] DeriveWrappingKey(string passphrase, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            RepositoryKeys.KeySize);
    }

    private static async Task WriteLineAsync(Stream destination, byte[] json, CancellationToken cancellationToken)
    {
        await destination.WriteAsync(json, cancellationToken);
        await destination.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    private static T? Deserialize<T>(string? line, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (string.IsNullOrEmpty(line))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize(line, typeInfo);
        }
        catch (JsonException exception)
        {
            throw new BackupIntegrityException("A line of the manifest cannot be read.", exception);
        }
    }

    private string CreateTemporaryPath()
    {
        Directory.CreateDirectory(options.TemporaryDirectory);
        return Path.Combine(options.TemporaryDirectory, $"{Guid.NewGuid():N}.object");
    }

    private static FileStream OpenTemporaryFile(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The temporary folder is emptied when the daemon starts.
        }
    }

    private sealed record EncodedObject(byte[] ContentSha256, long ContentLength, byte[] StoredSha256, long StoredLength);
}

/// <param name="TemporaryDirectory">
/// Folder where an object is encrypted before it is uploaded, so that the target can retry the upload and read its
/// length. It needs room for the largest object.
/// </param>
public sealed record BackupRepositoryOptions(string TemporaryDirectory)
{
    /// <summary>
    /// Iterations of PBKDF2 for a new repository. An existing repository keeps the number it was created with.
    /// </summary>
    public int KeyDerivationIterations { get; init; } = 600_000;
}

/// <param name="Name">Name of the object that holds the content.</param>
/// <param name="Sha256">SHA-256 of the content, as it was read, in lowercase hexadecimal.</param>
/// <param name="Size">Size of the content, as it was read.</param>
/// <param name="StoredSize">Size of the object, compressed and encrypted.</param>
/// <param name="Uploaded">Whether the object was uploaded: false when the repository already had it.</param>
public sealed record StoredObject(string Name, string Sha256, long Size, long StoredSize, bool Uploaded);

/// <param name="Completed">Objects checked so far.</param>
/// <param name="Total">Objects to check.</param>
public readonly record struct BackupProgress(long Completed, long Total);

/// <param name="Object">Name of the object.</param>
/// <param name="Path">Path of a file of the point held by the object.</param>
/// <param name="Message">What is wrong.</param>
public sealed record BackupVerificationProblem(string Object, string Path, string Message);

/// <param name="CheckedObjects">Distinct objects the point is made of, all checked.</param>
/// <param name="Problems">What was found wrong, at most <see cref="BackupRepository.MaxProblems"/>.</param>
/// <param name="ProblemCount">Every problem found, including the ones left out of <paramref name="Problems"/>.</param>
public sealed record BackupVerificationResult(
    int CheckedObjects,
    IReadOnlyList<BackupVerificationProblem> Problems,
    int ProblemCount)
{
    public bool Succeeded => ProblemCount == 0;
}
