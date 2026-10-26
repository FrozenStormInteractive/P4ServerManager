using System.Globalization;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Starts p4d, detached from the daemon, and waits until it accepts connections.
/// </summary>
public sealed class StartServerStep(
    P4ServerLauncher launcher,
    InstanceRuntimeRegistry runtimeRegistry,
    InstanceNotifier notifier) : IOperationStep<ProvisioningContext>
{
    public string Key => "start-server";

    public string Title => "Start the server";

    public async Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        int pid = await launcher.StartAsync(context.Instance, cancellationToken);
        context.ServerStarted = true;
        runtimeRegistry.Set(context.Instance.Id, InstanceRuntimeStatus.Running);
        await notifier.InstanceChangedAsync(context.Instance);
        return string.Create(CultureInfo.InvariantCulture, $"Listening on port {context.Instance.Port} (pid {pid})");
    }
}
