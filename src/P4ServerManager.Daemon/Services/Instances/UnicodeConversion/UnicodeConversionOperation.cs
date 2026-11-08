using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.UnicodeConversion;

/// <summary>
/// Converts an instance to Unicode mode: stop p4d, save a checkpoint, switch the database, start p4d again. A server
/// stopped on purpose stays stopped.
/// </summary>
public sealed class UnicodeConversionOperation(
    StopServerStep stopServer,
    SaveCheckpointStep saveCheckpoint,
    SwitchToUnicodeStep switchToUnicode,
    StartServerStep startServer) : IOperationDefinition<UnicodeConversionContext>
{
    public const string OperationKind = "convert-to-unicode";

    public string Kind => OperationKind;

    public OperationPlan<UnicodeConversionContext> Plan(UnicodeConversionContext context)
    {
        return new OperationPlan<UnicodeConversionContext>()
            .Then(stopServer, skipWhen: c => !c.WasRunning)
            .Then(saveCheckpoint)
            .Then(switchToUnicode)
            .Then(startServer, skipWhen: c => !c.WasRunning);
    }
}
