using System.Runtime.CompilerServices;

namespace P4ServerManager.Backup.Targets;

/// <summary>
/// A folder of the local file system, a mounted network share included. An object is written to a temporary file
/// next to it, then moved in place: it appears complete or not at all.
/// </summary>
public sealed class LocalBackupTarget : IBackupTarget
{
    private const int BufferSize = 81920;

    private readonly string rootPath;

    public TargetCapabilities Capabilities => TargetCapabilities.ConditionalWrite | TargetCapabilities.AtomicWrite;

    public string Location => rootPath;
    
    /// <param name="rootPath">Absolute path of the folder. It is created on the first write.</param>
    public LocalBackupTarget(string rootPath)
    {
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The folder of a local target must be an absolute path.", nameof(rootPath));
        }
        
        this.rootPath = Path.GetFullPath(rootPath);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        return Task.FromResult(File.Exists(GetPath(key)));
    }

    public async Task PutAsync(
        string key,
        Stream content,
        BackupPutOptions options,
        CancellationToken cancellationToken)
    {
        string path = GetPath(key);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (options.IfNotExists && File.Exists(path))
        {
            throw new BackupObjectExistsException(key);
        }

        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (FileStream file = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous))
            {
                await content.CopyToAsync(file, BufferSize, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }

            try
            {
                File.Move(temporaryPath, path, overwrite: !options.IfNotExists);
            }
            catch (IOException) when (options.IfNotExists && File.Exists(path))
            {
                throw new BackupObjectExistsException(key);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        string path = GetPath(key);
        try
        {
            Stream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult(stream);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new BackupObjectNotFoundException(key);
        }
    }

    public async IAsyncEnumerable<BackupObjectInfo> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        BackupObjectKey.ValidatePrefix(prefix);
        int separator = prefix.LastIndexOf('/');
        string directory = separator < 0
            ? rootPath
            : Path.Combine(rootPath, prefix[..separator].Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        EnumerationOptions enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = false };
        foreach (string file in Directory.EnumerateFiles(directory, "*", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = Path.GetRelativePath(rootPath, file).Replace(Path.DirectorySeparatorChar, '/');
            // Temporary files, and anything else that is not an object, are left out.
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || !BackupObjectKey.IsValid(key))
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (FileNotFoundException)
            {
                // Deleted since the folder was read.
                continue;
            }
            yield return new BackupObjectInfo(key, size);
        }
        await Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        string path = GetPath(key);
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing to delete.
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private string GetPath(string key)
    {
        BackupObjectKey.Validate(key);
        return Path.Combine(rootPath, key.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A temporary file left behind is ignored by the listing.
        }
    }
}
