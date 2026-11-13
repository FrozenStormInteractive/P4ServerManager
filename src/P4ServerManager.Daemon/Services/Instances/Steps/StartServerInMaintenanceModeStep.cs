using System.Globalization;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Steps;

/// <summary>
/// Starts p4d in maintenance mode (<c>p4d -n</c>) for this run only, whatever the instance says, and waits until it
/// accepts connections: for the steps of an operation that need p4d but no client workspace, while its users cannot
/// work on it. The maintenance mode of the instance is left as it is: <see cref="StopServerStep"/> then
/// <see cref="StartServerStep"/> start p4d as the instance says. The instance is shown as running, unless it is in a
/// transitional status that the operation ends itself.
/// </summary>
public sealed class StartServerInMaintenanceModeStep(
    P4ServerLauncher launcher,
    InstanceRuntimeRegistry runtimeRegistry,
    InstanceNotifier notifier) : IOperationStep<IInstanceOperationContext>
{
    public string Key => "start-server-maintenance";

    public string Title => "Start the server in maintenance mode";

    public async Task<string?> RunAsync(
        IInstanceOperationContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        int pid = await launcher.StartAsync(context.Instance, cancellationToken, maintenanceMode: true);
        if (!runtimeRegistry.Get(context.Instance.Id).IsTransitional())
        {
            runtimeRegistry.Set(context.Instance.Id, InstanceRuntimeStatus.Running);
        }
        await notifier.InstanceChangedAsync(context.Instance);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Listening on port {context.Instance.Port} in maintenance mode (pid {pid})");
    }
}
