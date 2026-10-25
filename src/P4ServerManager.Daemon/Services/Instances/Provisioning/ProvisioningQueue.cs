using System.Threading.Channels;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <param name="Name">User name.</param>
/// <param name="Password">Password. It only lives in memory until p4d has it.</param>
public sealed record SuperUserCredentials(string Name, string Password)
{
    // Keeps the password out of logs that print the record.
    public override string ToString()
    {
        return $"SuperUserCredentials {{ Name = {Name} }}";
    }
}

/// <summary>
/// How an instance comes to life. Each kind has its own steps.
/// </summary>
public enum ProvisioningKind
{
    /// <summary>
    /// A new server: a new database and its super user.
    /// </summary>
    Create,

    /// <summary>
    /// An existing root, taken over as it is: its database is upgraded to the build of the instance.
    /// </summary>
    Import,
}

/// <param name="InstanceId">Instance to provision, saved in the Provisioning state.</param>
/// <param name="Kind">Whether the server is created or imported.</param>
/// <param name="SuperUser">Super user to create. Required to create a server, unused to import one.</param>
public sealed record ProvisioningRequest(
    Guid InstanceId,
    ProvisioningKind Kind,
    SuperUserCredentials? SuperUser = null);

/// <summary>
/// Instances waiting to be provisioned by the <see cref="ProvisioningWorker"/>.
/// </summary>
public sealed class ProvisioningQueue
{
    private readonly Channel<ProvisioningRequest> channel = Channel.CreateUnbounded<ProvisioningRequest>();

    public ValueTask EnqueueAsync(ProvisioningRequest request, CancellationToken cancellationToken)
    {
        return channel.Writer.WriteAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<ProvisioningRequest> ReadAllAsync(CancellationToken cancellationToken)
    {
        return channel.Reader.ReadAllAsync(cancellationToken);
    }
}
