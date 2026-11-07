using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.UnicodeConversion;

/// <summary>
/// Stops p4d: <c>p4d -xi</c> works on the database directly. Skipped when p4d did not run.
/// </summary>
public sealed class StopServerForConversionStep(P4ServerLauncher launcher) : IOperationStep<UnicodeConversionContext>
{
    public string Key => "stop-server";

    public string Title => "Stop the server";

    public bool ShouldRun(UnicodeConversionContext context)
    {
        return context.WasRunning;
    }

    public async Task<string?> RunAsync(
        UnicodeConversionContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        await launcher.StopAsync(context.Instance, cancellationToken);
        return null;
    }
}
