using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

/// <summary>
/// Moves an instance to another p4d build with the <see cref="UpgradeOperation"/>. The <see cref="InstanceSupervisor"/>
/// runs it while it holds the instance.
/// </summary>
public sealed class InstanceUpgrader(
    UpgradeOperation operation,
    OperationRunner operationRunner,
    P4ServerLauncher launcher,
    ILogger<InstanceUpgrader> logger)
{
    /// <summary>
    /// Runs the upgrade. The operation is begun before the first await, so its snapshot replaces the previous one as
    /// soon as this returns. A failure is kept as the last error of the instance, which is not saved: the caller saves
    /// it. Before the database is touched, a failure leaves the instance on its previous build, and starts p4d again
    /// if it ran.
    /// </summary>
    public async Task UpgradeAsync(UpgradeContext context, CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        try
        {
            Task run = operationRunner.RunAsync(
                instance.Id,
                operation,
                context,
                cancellationToken);
            logger.LogInformation(
                "Upgrading instance {Name} from p4d {Version}/{Build} to {Release}",
                instance.Name,
                context.PreviousBuild.Version,
                context.PreviousBuild.BuildNumber,
                context.Release);
            await run;

            if (context.UpToDate)
            {
                // Nothing was done: the last error of an earlier operation is kept.
                logger.LogInformation(
                    "Instance {Name} already runs p4d {Version}/{Build}",
                    instance.Name,
                    instance.Build?.Version,
                    instance.Build?.BuildNumber);
                return;
            }

            instance.LastError = null;
            logger.LogInformation(
                "Upgraded instance {Name} to p4d {Version}/{Build}",
                instance.Name,
                instance.Build?.Version,
                instance.Build?.BuildNumber);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Upgrade of instance {Name} failed", instance.Name);
            instance.LastError = context.DatabaseUpgradeStarted && !context.DatabaseUpgraded
                ? $"{exception.Message} The database may be partly upgraded."
                : exception.Message;

            if (!context.DatabaseUpgradeStarted && context.WasRunning && !launcher.IsRunning(instance))
            {
                await RestartPreviousBuildAsync(instance, cancellationToken);
            }
        }
    }

    private async Task RestartPreviousBuildAsync(P4ServerInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            await launcher.StartAsync(instance, cancellationToken);
            logger.LogInformation("Started instance {Name} again on its previous build", instance.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not start instance {Name} again on its previous build", instance.Name);
            instance.LastError += $" It could not be started again: {exception.Message}";
        }
    }
}
