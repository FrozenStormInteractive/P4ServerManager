using P4ServerManager.Cli.Operations;
using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Tests.Operations;

public sealed class OperationProgressWriterTests
{
    private readonly StringWriter _writer = new() { NewLine = "\n" };

    [Fact]
    public void Report_WritesALinePerChangeOfStatus()
    {
        OperationProgressWriter progress = new(_writer, rewriteInPlace: false);

        progress.Report(Snapshot(OperationStepStatus.Running, OperationStepStatus.Pending, percent: 10));
        progress.Report(Snapshot(OperationStepStatus.Running, OperationStepStatus.Pending, percent: 60));
        progress.Report(Snapshot(OperationStepStatus.Succeeded, OperationStepStatus.Skipped));
        progress.Complete();

        _writer.ToString().ShouldBe("[ .. ] Download p4d\n[ ok ] Download p4d\n[skip] Upgrade the database\n");
    }

    [Fact]
    public void Report_RewritesTheRunningStepInPlaceOnATerminal()
    {
        OperationProgressWriter progress = new(_writer, rewriteInPlace: true);

        progress.Report(Snapshot(OperationStepStatus.Running, OperationStepStatus.Pending, percent: 10));
        progress.Report(Snapshot(OperationStepStatus.Running, OperationStepStatus.Pending, percent: 60));
        progress.Report(Snapshot(OperationStepStatus.Succeeded, OperationStepStatus.Running));
        progress.Complete();

        _writer.ToString().ShouldBe(
            "[ .. ] Download p4d 10%\r[ .. ] Download p4d 60%\r[ ok ] Download p4d    \n"
            + "[ .. ] Upgrade the database\n");
    }

    private static OperationSnapshot Snapshot(
        OperationStepStatus download,
        OperationStepStatus upgrade,
        int? percent = null)
    {
        string? current = download == OperationStepStatus.Running ? "download"
            : upgrade == OperationStepStatus.Running ? "upgrade"
            : null;
        return new OperationSnapshot(
            Guid.Empty,
            "provision",
            OperationStatus.Running,
            [
                new OperationStepState("download", "Download p4d", download, null),
                new OperationStepState("upgrade", "Upgrade the database", upgrade, null),
            ],
            current,
            percent,
            null);
    }
}
