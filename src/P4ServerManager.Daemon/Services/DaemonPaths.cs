using Microsoft.Extensions.Options;

using P4ServerManager.Daemon.Options;

namespace P4ServerManager.Daemon.Services;

/// <summary>
/// Absolute paths inside the data directory.
/// </summary>
public sealed class DaemonPaths
{
    public DaemonPaths(IOptions<DaemonOptions> options, IHostEnvironment environment)
        : this(Path.GetFullPath(options.Value.DataDirectory, environment.ContentRootPath))
    {
    }

    public DaemonPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "p4servermanager.db");

    public string BuildsDirectory => Path.Combine(DataDirectory, "builds");

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

    public string GetBuildDirectory(string platform, string version, string build)
    {
        return Path.Combine(BuildsDirectory, platform, version, build);
    }

    public string GetInstanceDirectory(Guid instanceId)
    {
        return Path.Combine(InstancesDirectory, instanceId.ToString("N"));
    }

    public string GetDefaultRootPath(Guid instanceId)
    {
        return Path.Combine(GetInstanceDirectory(instanceId), "root");
    }

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
