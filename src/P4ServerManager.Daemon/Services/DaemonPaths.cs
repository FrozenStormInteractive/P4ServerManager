using System.Globalization;

using Microsoft.Extensions.Options;

using P4ServerManager.Daemon.Options;
using P4ServerManager.Daemon.Services.Instances;

namespace P4ServerManager.Daemon.Services;

/// <summary>
/// Absolute paths inside the data directory.
/// </summary>
public sealed class DaemonPaths
{
    public string DataDirectory { get; }

    /// <summary>
    /// Unix domain socket of the CLI, or null when the daemon listens on none.
    /// </summary>
    public string? SocketPath { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "p4servermanager.db");

    /// <summary>
    /// Extracted p4d builds, ready to run. p4d is recognized as such by its executable under this folder.
    /// </summary>
    public string BuildsDirectory => Path.Combine(DataDirectory, "builds");

    /// <summary>
    /// Compressed p4d builds.
    /// </summary>
    public string ArchiveDirectory { get; }

    public string InstancesDirectory => Path.Combine(DataDirectory, "instances");
    
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
            options.Value.ArchiveDirectory)
    {
    }

    public DaemonPaths(
        string dataDirectory,
        string? socketPath = DaemonOptions.DefaultSocketPath,
        string? archiveDirectory = null)
    {
        DataDirectory = dataDirectory;
        SocketPath = string.IsNullOrEmpty(socketPath) ? null : Path.GetFullPath(socketPath, dataDirectory);
        ArchiveDirectory = Path.GetFullPath(
            string.IsNullOrEmpty(archiveDirectory) ? "archive" : archiveDirectory,
            dataDirectory);
    }

    public string GetBuildDirectory(string platform, string version, string build)
    {
        return Path.Combine(BuildsDirectory, platform, version, build);
    }

    public string GetArchivePath(string platform, string version, string build)
    {
        return Path.Combine(ArchiveDirectory, platform, version, build + ".tar.zst");
    }

    public string GetInstanceDirectory(Guid instanceId)
    {
        return Path.Combine(InstancesDirectory, instanceId.ToString("N"));
    }

    /// <summary>
    /// Root of an instance created without a server directory: the folder of the instance is its server directory.
    /// </summary>
    public string GetDefaultRootPath(Guid instanceId)
    {
        return ServerLayout.GetRootPath(GetInstanceDirectory(instanceId));
    }

    /// <summary>
    /// Log of a p4d whose server directory has no logs folder, such as an imported server.
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
        return Path.Combine(GetInstanceDirectory(instanceId), "logs", "p4d-start.log");
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

    public string GetPidFilePath(Guid instanceId)
    {
        return Path.Combine(GetInstanceDirectory(instanceId), "p4d.pid");
    }

    /// <summary>
    /// Folder for the p4 client state (tickets) of an instance, so the daemon never touches the user's own.
    /// </summary>
    public string GetClientStateDirectory(Guid instanceId)
    {
        return Path.Combine(GetInstanceDirectory(instanceId), "client");
    }
}
