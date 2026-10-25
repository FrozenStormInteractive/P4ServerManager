using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// State shared by the provisioning steps of one instance.
/// </summary>
/// <param name="db">Context that tracks <paramref name="instance"/>.</param>
/// <param name="instance">The instance, in the Provisioning state.</param>
/// <param name="superUser">Super user to create, when the server is created.</param>
public sealed class ProvisioningContext(DaemonDbContext db, P4ServerInstance instance, SuperUserCredentials? superUser)
{
    public DaemonDbContext Db { get; } = db;

    public P4ServerInstance Instance { get; } = instance;

    public SuperUserCredentials? SuperUser { get; } = superUser;

    /// <summary>
    /// Set once p4d runs, so that a failure of a later step stops it.
    /// </summary>
    public bool ServerStarted { get; set; }
}
