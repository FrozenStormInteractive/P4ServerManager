using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Steps;

/// <summary>
/// Stops p4d, so that nothing writes to the database while the next steps work on it.
/// </summary>
public sealed class StopServerStep(P4ServerLauncher launcher) : IOperationStep<IInstanceOperationContext>
{
    public string Key => "stop-server";

    public string Title => "Stop the server";

    public async Task<string?> RunAsync(
        IInstanceOperationContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        await launcher.StopAsync(context.Instance, cancellationToken);
        return null;
    }
}
