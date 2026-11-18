using System.Globalization;

using Microsoft.Extensions.Options;

using P4ServerManager.Daemon.Options;
using P4ServerManager.Daemon.Services.Instances;

namespace P4ServerManager.Daemon.Services;

/// <summary>
/// Absolute paths of the files of the daemon. They fall in four folders, so that a package puts each where its platform
/// keeps that kind of file: the data directory (what cannot be rebuilt), the cache directory (what can), the runtime
/// directory (what only matters while the daemon and the servers run) and the servers directory (the default server
/// directories). The last three default to the data directory, or a folder of it.
/// </summary>
public sealed class DaemonPaths
{
    public string DataDirectory { get; }

    /// <summary>
    /// Folder of the extracted p4d builds, which the daemon extracts again from the archive.
    /// </summary>
    public string CacheDirectory { get; }

    /// <summary>
    /// Folder of the pid files, the start output of p4d and the temporary files.
    /// </summary>
    public string RuntimeDirectory { get; }

    /// <summary>
    /// Folder of the default server directories, one per instance, named after the instance at its creation.
    /// </summary>
    public string ServersDirectory { get; }

    /// <summary>
    /// Unix domain socket of the CLI, or null when the daemon listens on none.
    /// </summary>
    public string? SocketPath { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "p4servermanager.db");

    /// <summary>
    /// Extracted p4d builds, ready to run. p4d is recognized as such by its executable under this folder.
    /// </summary>
    public string BuildsDirectory => Path.Combine(CacheDirectory, "builds");

    /// <summary>
    /// Compressed p4d builds.
    /// </summary>
    public string ArchiveDirectory { get; }

    /// <summary>
    /// The files the daemon keeps for each instance, one folder per instance. Never a server directory.
    /// </summary>
    public string InstancesDirectory => Path.Combine(DataDirectory, "instances");

    /// <summary>
    /// The runtime files of each instance, one folder per instance.
    /// </summary>
    public string RuntimeInstancesDirectory => Path.Combine(RuntimeDirectory, "instances");

    /// <summary>
    /// Keys of ASP.NET Core Data Protection, which encrypt the stored passwords. Without them, the passwords cannot be
    /// read: a backup of the data directory must include them.
    /// </summary>
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");

    /// <summary>
    /// Profiles of the p4dctl-ng servers of the HelixCoreServer Synology package, copied there by the package of the
    /// daemon, to be imported.
    /// </summary>
    public string LegacyProfilesDirectory => Path.Combine(DataDirectory, "import", "HelixCoreServer");

    /// <summary>
    /// Profiles the user chose not to import.
    /// </summary>
    public string DismissedLegacyProfilesDirectory => Path.Combine(LegacyProfilesDirectory, "dismissed");

    public DaemonPaths(IOptions<DaemonOptions> options, IHostEnvironment environment)
        : this(
            Path.GetFullPath(options.Value.DataDirectory, environment.ContentRootPath),
            options.Value.SocketPath,
            options.Value.ArchiveDirectory,
            options.Value.CacheDirectory,
            options.Value.RuntimeDirectory,
            options.Value.ServersDirectory)
    {
    }

    public DaemonPaths(
        string dataDirectory,
        string? socketPath = DaemonOptions.DefaultSocketPath,
        string? archiveDirectory = null,
        string? cacheDirectory = null,
        string? runtimeDirectory = null,
        string? serversDirectory = null)
    {
        DataDirectory = dataDirectory;
        ArchiveDirectory = Resolve(archiveDirectory, "archive");
        CacheDirectory = Resolve(cacheDirectory, ".");
        RuntimeDirectory = Resolve(runtimeDirectory, ".");
        ServersDirectory = Resolve(serversDirectory, "servers");
        SocketPath = string.IsNullOrEmpty(socketPath) ? null : Path.GetFullPath(socketPath, RuntimeDirectory);
    }

    private string Resolve(string? path, string defaultPath)
    {
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(string.IsNullOrEmpty(path) ? defaultPath : path, DataDirectory));
    }

    public string GetBuildDirectory(string platform, string version, string build)
    {
        return Path.Combine(BuildsDirectory, platform, version, build);
    }

    public string GetArchivePath(string platform, string version, string build)
    {
        return Path.Combine(ArchiveDirectory, platform, version, build + ".tar.zst");
    }

    /// <summary>
    /// The files the daemon keeps for an instance, in the data directory.
    /// </summary>
    public string GetInstanceDirectory(Guid instanceId)
    {
        return Path.Combine(InstancesDirectory, instanceId.ToString("N"));
    }

    /// <summary>
    /// The runtime files of an instance, in the runtime directory.
    /// </summary>
    public string GetRuntimeInstanceDirectory(Guid instanceId)
    {
        return Path.Combine(RuntimeInstancesDirectory, instanceId.ToString("N"));
    }

    /// <summary>
    /// Server directory of an instance created without one: the folder of the servers directory named after it.
    /// </summary>
    public string GetDefaultServerDirectory(string instanceName)
    {
        return Path.Combine(ServersDirectory, instanceName);
    }

    /// <summary>
    /// Root of an instance created without a server directory, in <see cref="GetDefaultServerDirectory"/>.
    /// </summary>
    public string GetDefaultRootPath(string instanceName)
    {
        return ServerLayout.GetRootPath(GetDefaultServerDirectory(instanceName));
    }

    /// <summary>
    /// Log of a p4d whose server directory has no logs folder, such as an imported server. When the instance is deleted
    /// and its files are kept, the folder of this log is moved to the <c>logs</c> folder of the root.
    /// </summary>
    public string GetLogPath(Guid instanceId)
    {
        return Path.Combine(GetInstanceDirectory(instanceId), "logs", "p4d.log");
    }

    /// <summary>
    /// What p4d prints while it starts, before it writes to its log: a port already in use, for example.
    /// </summary>
    public string GetStartOutputPath(Guid instanceId)
    {
        return Path.Combine(GetRuntimeInstanceDirectory(instanceId), "p4d-start.log");
    }

    /// <summary>
    /// Checkpoint of the database of an imported server, taken before the daemon upgrades it.
    /// </summary>
    public string GetImportCheckpointPath(Guid instanceId)
    {
        return Path.Combine(GetInstanceDirectory(instanceId), "checkpoint-before-import.gz");
    }

    /// <summary>
    /// Checkpoint of the database of a server, taken before it is converted to Unicode mode. Each attempt has its own file.
    /// </summary>
    public string GetUnicodeCheckpointPath(Guid instanceId, DateTimeOffset savedAt)
    {
        string timestamp = savedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return Path.Combine(GetInstanceDirectory(instanceId), $"checkpoint-before-unicode-{timestamp}.gz");
    }

    /// <summary>
    /// Fix journal of a health check. It is not the <c>jnl.fix</c> of the root: it would replace the one an earlier
    /// check, or an administrator, left there.
    /// </summary>
    public string GetHealthCheckFixJournalPath(Guid instanceId, Guid runId)
    {
        return Path.Combine(GetInstanceDirectory(instanceId), "health-checks", $"{runId:N}.jnl.fix");
    }

    /// <summary>
    /// Where the objects of a backup are encrypted before they are uploaded. Emptied when the daemon starts.
    /// </summary>
    public string BackupTemporaryDirectory => Path.Combine(RuntimeDirectory, "tmp", "backups");

    /// <summary>
    /// Size, date and SHA-256 of the files of an instance at its last backup, so that only the files that changed are
    /// read again. Rebuilt when it is missing.
    /// </summary>
    public string GetBackupIndexPath(Guid instanceId)
    {
        return Path.Combine(GetInstanceDirectory(instanceId), "backup-index.jsonl");
    }

    /// <summary>
    /// The folders a restore moved aside, while it runs: a later daemon process puts them back when the restore did not
    /// end.
    /// </summary>
    public string GetRestoreStatePath(Guid instanceId)
    {
        return Path.Combine(GetInstanceDirectory(instanceId), "restore-state.json");
    }

    /// <summary>
    /// A deletion of the instance that has begun: a later daemon process carries it on when it did not end.
    /// </summary>
    public string GetDeletionStatePath(Guid instanceId)
    {
        return Path.Combine(GetInstanceDirectory(instanceId), "deletion.json");
    }

    public string GetPidFilePath(Guid instanceId)
    {
        return Path.Combine(GetRuntimeInstanceDirectory(instanceId), "p4d.pid");
    }

    /// <summary>
    /// Folder for the p4 client state (tickets) of an instance, so the daemon never touches the user's own.
    /// </summary>
    public string GetClientStateDirectory(Guid instanceId)
    {
        return Path.Combine(GetInstanceDirectory(instanceId), "client");
    }
}
