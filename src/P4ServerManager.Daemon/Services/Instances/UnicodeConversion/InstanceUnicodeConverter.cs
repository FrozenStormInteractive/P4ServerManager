using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.UnicodeConversion;

/// <summary>
/// Converts an instance to Unicode mode by running the conversion steps, in the order they were registered by
/// <see cref="UnicodeConversionServiceCollectionExtensions.AddInstanceUnicodeConversionServices"/>: stop p4d, save a
/// checkpoint, switch the database, start p4d again. The <see cref="InstanceSupervisor"/> runs it while it holds the
/// instance.
/// </summary>
public sealed class InstanceUnicodeConverter(
    IEnumerable<IOperationStep<UnicodeConversionContext>> steps,
    OperationRunner operationRunner,
    P4ServerLauncher launcher,
    ILogger<InstanceUnicodeConverter> logger)
{
    public const string OperationKind = "convert-to-unicode";

    private readonly IReadOnlyList<IOperationStep<UnicodeConversionContext>> conversionSteps = steps.ToList();

    /// <summary>
    /// Runs the conversion. The operation is begun before the first await, so its snapshot replaces the previous one as
    /// soon as this returns. A failure is kept as the last error of the instance, which is not saved: the caller saves
    /// it. p4d is started again if it ran, whether the database was converted or not: <c>p4d -xi</c> checks the metadata
    /// before it changes anything.
    /// </summary>
    public async Task ConvertAsync(UnicodeConversionContext context, CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        try
        {
            Task operation = operationRunner.RunAsync(
                instance.Id,
                OperationKind,
                conversionSteps,
                context,
                cancellationToken);
            logger.LogInformation("Converting instance {Name} to Unicode mode", instance.Name);
            await operation;

            instance.LastError = null;
            logger.LogInformation("Converted instance {Name} to Unicode mode", instance.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Conversion of instance {Name} to Unicode mode failed", instance.Name);
            instance.LastError = exception.Message;

            if (context.WasRunning && !instance.Unicode && !launcher.IsRunning(instance))
            {
                await RestartAsync(instance, cancellationToken);
            }
        }
    }

    private async Task RestartAsync(P4ServerInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            await launcher.StartAsync(instance, cancellationToken);
            logger.LogInformation("Started instance {Name} again, not in Unicode mode", instance.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not start instance {Name} again", instance.Name);
            instance.LastError += $" It could not be started again: {exception.Message}";
        }
    }
}
