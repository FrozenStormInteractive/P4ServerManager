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

/// <param name="InstanceId">Instance to provision, saved in the Provisioning state.</param>
/// <param name="SuperUser">Super user to create.</param>
public sealed record ProvisioningRequest(Guid InstanceId, SuperUserCredentials SuperUser);

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
