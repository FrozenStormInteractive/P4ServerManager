using System.Globalization;

using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

/// <summary>
/// Starts p4d again with the target build, and waits until it accepts connections. Skipped when p4d did not run before
/// the upgrade: a server stopped on purpose stays stopped.
/// </summary>
public sealed class StartUpgradedServerStep(P4ServerLauncher launcher) : IOperationStep<UpgradeContext>
{
    public string Key => "start-server";

    public string Title => "Start the server";

    public bool ShouldRun(UpgradeContext context)
    {
        return context.WasRunning && !context.UpToDate;
    }

    public async Task<string?> RunAsync(
        UpgradeContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        int pid = await launcher.StartAsync(context.Instance, cancellationToken);
        return string.Create(CultureInfo.InvariantCulture, $"Listening on port {context.Instance.Port} (pid {pid})");
    }
}
