using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

/// <summary>
/// Stops p4d, so that nothing writes to the database while it is saved and upgraded. Skipped when p4d did not run.
/// </summary>
public sealed class StopServerStep(P4ServerLauncher launcher) : IOperationStep<UpgradeContext>
{
    public string Key => "stop-server";

    public string Title => "Stop the server";

    public bool ShouldRun(UpgradeContext context)
    {
        return context.WasRunning && !context.UpToDate;
    }

    public async Task<string?> RunAsync(
        UpgradeContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        await launcher.StopAsync(context.Instance, cancellationToken);
        return null;
    }
}
