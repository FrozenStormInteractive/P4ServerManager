namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Folders of a server: the P4ROOT is the <c>root</c> subfolder of the server directory, next to the journals, the logs
/// and the archives of the depots. The configurables of the server point to them relative to the root, through
/// <c>../</c>.
/// </summary>
public static class ServerLayout
{
    public const string RootFolder = "root";

    public const string JournalsFolder = "journals";

    public const string LogsFolder = "logs";

    public const string ArchivesFolder = "archives";

    /// <summary>
    /// Folder of the private key and the certificate, inside the root.
    /// </summary>
    public const string SslFolder = "ssl";

    /// <summary>
    /// Name of the p4d log in the logs folder.
    /// </summary>
    public const string LogFile = "log";

    /// <summary>
    /// Name of the file of the server ID in the root.
    /// </summary>
    public const string ServerIdFile = "server.id";

    /// <summary>
    /// Folders next to the root.
    /// </summary>
    public static readonly IReadOnlyList<string> SiblingFolders = [JournalsFolder, LogsFolder, ArchivesFolder];

    public static string GetRootPath(string serverDirectory)
    {
        return Path.Combine(serverDirectory, RootFolder);
    }

    /// <returns>The folder that holds the root, or null when the root is the root of a file system.</returns>
    public static string? GetServerDirectory(string rootPath)
    {
        return Path.GetDirectoryName(Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar));
    }

    public static string GetSslDirectory(string rootPath)
    {
        return Path.Combine(rootPath, SslFolder);
    }

    /// <summary>
    /// File in the root where p4d keeps its server ID, as <c>p4d -xD</c> sets it.
    /// </summary>
    public static string GetServerIdPath(string rootPath)
    {
        return Path.Combine(rootPath, ServerIdFile);
    }

    /// <summary>
    /// Reads the server ID of p4d from the root.
    /// </summary>
    /// <returns>The server ID, or null when the root has no server ID.</returns>
    public static async Task<string?> ReadServerIdAsync(string rootPath, CancellationToken cancellationToken)
    {
        string path = GetServerIdPath(rootPath);
        if (!File.Exists(path))
        {
            return null;
        }

        // Every read of an instance reads the file: it must not stop the change of the server ID from replacing it.
        string content;
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                useAsync: true);
            using StreamReader reader = new(stream);
            content = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        string? serverId = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrEmpty(serverId) ? null : serverId;
    }

    /// <summary>
    /// Reads the server ID of p4d from the root, as <see cref="ReadServerIdAsync"/> does, for display: a root that
    /// cannot be read leaves the server ID unknown instead of failing.
    /// </summary>
    /// <returns>The server ID, or null when the root has none or cannot be read.</returns>
    public static async Task<string?> TryReadServerIdAsync(string rootPath, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadServerIdAsync(rootPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
