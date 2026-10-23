namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Provisions the queued instances, a few at a time.
/// </summary>
public sealed class ProvisioningWorker(
    ProvisioningQueue queue,
    InstanceProvisioner provisioner,
    ILogger<ProvisioningWorker> logger) : BackgroundService
{
    private const int MaxConcurrentProvisionings = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = MaxConcurrentProvisionings,
            CancellationToken = stoppingToken,
        };
        try
        {
            await Parallel.ForEachAsync(queue.ReadAllAsync(stoppingToken), options, async (request, cancellationToken) =>
            {
                try
                {
                    await provisioner.ProvisionAsync(request, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Provisioning of instance {Id} failed", request.InstanceId);
                }
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The daemon stops. Instances left in Provisioning are marked Failed at the next start.
        }
    }
}
