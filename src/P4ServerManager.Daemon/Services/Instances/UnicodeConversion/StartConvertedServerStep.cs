using System.Globalization;

using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.UnicodeConversion;

/// <summary>
/// Starts p4d again in Unicode mode, and waits until it accepts connections. Skipped when p4d did not run before the
/// conversion: a server stopped on purpose stays stopped.
/// </summary>
public sealed class StartConvertedServerStep(P4ServerLauncher launcher) : IOperationStep<UnicodeConversionContext>
{
    public string Key => "start-server";

    public string Title => "Start the server";

    public bool ShouldRun(UnicodeConversionContext context)
    {
        return context.WasRunning;
    }

    public async Task<string?> RunAsync(
        UnicodeConversionContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        int pid = await launcher.StartAsync(context.Instance, cancellationToken);
        return string.Create(CultureInfo.InvariantCulture, $"Listening on port {context.Instance.Port} (pid {pid})");
    }
}
