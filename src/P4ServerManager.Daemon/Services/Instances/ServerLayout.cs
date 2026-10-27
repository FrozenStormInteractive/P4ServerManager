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
}
