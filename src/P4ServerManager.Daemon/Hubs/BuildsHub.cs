using Microsoft.AspNetCore.SignalR;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Hubs;

/// <summary>
/// Pushes the changes of the p4d builds and of the archive worker to the clients. Clients only listen.
/// </summary>
public sealed class BuildsHub : Hub<IBuildsClient>
{
    public const string Path = "/hubs/builds";
}

public interface IBuildsClient
{
    Task BuildUpdated(BuildDto build);

    Task BuildDeleted(Guid buildId);

    Task ArchiveStatusChanged(BuildArchiveStatusDto status);
}
