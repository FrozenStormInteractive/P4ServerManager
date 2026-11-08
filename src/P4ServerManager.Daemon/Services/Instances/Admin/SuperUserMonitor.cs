using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Options;

namespace P4ServerManager.Daemon.Services.Instances.Admin;

/// <summary>
/// Checks the stored super user of each running instance: at startup, every
/// <see cref="DaemonOptions.SuperUserCheckInterval"/>, each time a server starts, and on request. The status reaches
/// the clients with the instance.
/// </summary>
public sealed class SuperUserMonitor(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    SuperUserService superUsers,
    InstanceRuntimeRegistry runtimeRegistry,
    IOptions<DaemonOptions> options,
    TimeProvider timeProvider,
    ILogger<SuperUserMonitor> logger) : BackgroundService
{
    private readonly Channel<Guid> requests = Channel.CreateUnbounded<Guid>();

    /// <summary>
    /// Asks for a check of the super user of an instance, as soon as possible.
    /// </summary>
    public void RequestCheck(Guid instanceId)
    {
        requests.Writer.TryWrite(instanceId);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        runtimeRegistry.Started += RequestCheck;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        runtimeRegistry.Started -= RequestCheck;
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAllAsync(stoppingToken);
            await RunRequestsUntilNextRoundAsync(stoppingToken);
        }
    }

    private async Task CheckAllAsync(CancellationToken stoppingToken)
    {
        List<Guid> instanceIds;
        try
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(stoppingToken);
            instanceIds = await db.Instances
                .Where(i => i.State == P4ServerInstanceState.Ready && i.SuperUser != null)
                .Select(i => i.Id)
                .ToListAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not list the instances to check their super user");
            return;
        }

        foreach (Guid instanceId in instanceIds)
        {
            await CheckAsync(instanceId, stoppingToken);
        }
    }

    private async Task RunRequestsUntilNextRoundAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource round = new(options.Value.SuperUserCheckInterval, timeProvider);
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, round.Token);
        try
        {
            while (true)
            {
                Guid instanceId = await requests.Reader.ReadAsync(wait.Token);
                await CheckAsync(instanceId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Time for the next round.
        }
    }

    private async Task CheckAsync(Guid instanceId, CancellationToken stoppingToken)
    {
        try
        {
            await superUsers.TryCheckAsync(instanceId, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not check the super user of instance {InstanceId}", instanceId);
        }
    }
}
