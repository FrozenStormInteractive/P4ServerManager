using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Reads the configurables of a server with <c>p4d -cshow</c> on its root, without any user: it works whether p4d runs
/// or not. Every read of <c>db.config</c> by the daemon goes through here.
/// </summary>
public sealed class ServerConfigurationReader(IP4ServerBuildCache buildCache, IProcessRunner processRunner)
{
    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    /// <param name="cancellationToken">Aborts the read.</param>
    /// <exception cref="InvalidDataException">p4d printed a line that is not a configurable.</exception>
    public async Task<ServerConfiguration> ReadAsync(P4ServerInstance instance, CancellationToken cancellationToken)
    {
        // The values of the server ID win over the ones of every server.
        string? serverId = await ServerLayout.ReadServerIdAsync(instance.RootPath, cancellationToken);
        ProcessResult result = await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(instance.Build!),
                [.. P4ServerLauncher.GetRootArguments(instance), "-cshow"],
                Timeout: ProcessRunnerExtensions.CommandTimeout),
            cancellationToken);
        return new ServerConfiguration(serverId, ConfigShowParser.Parse(result.StandardOutput, serverId));
    }
}

/// <summary>
/// The configurables of a server, as they apply to it.
/// </summary>
/// <param name="ServerId">Server ID of p4d, or null when it has none.</param>
/// <param name="Configurables">Its configurables: its own values, then the ones of every server.</param>
public sealed record ServerConfiguration(string? ServerId, IReadOnlyDictionary<string, string> Configurables)
{
    /// <summary>
    /// Whether the server runs in Unicode mode: <c>p4d -xi</c> sets the <c>unicode</c> configurable.
    /// </summary>
    public bool IsUnicode => Configurables.TryGetValue("unicode", out string? value) && value == "1";
}
