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

    [Fact]
    public void Report_WritesEachOccurrenceOfAStepThatRunsMoreThanOnce()
    {
        OperationProgressWriter progress = new(_writer, rewriteInPlace: true);

        progress.Report(StopStartStop(OperationStepStatus.Running, OperationStepStatus.Pending, OperationStepStatus.Pending));
        progress.Report(StopStartStop(OperationStepStatus.Succeeded, OperationStepStatus.Succeeded, OperationStepStatus.Running));
        progress.Report(StopStartStop(OperationStepStatus.Succeeded, OperationStepStatus.Succeeded, OperationStepStatus.Succeeded));
        progress.Complete();

        _writer.ToString().ShouldBe(
            "[ .. ] Stop the server\r[ ok ] Stop the server\n"
            + "[ ok ] Start the server\n"
            + "[ .. ] Stop the server\r[ ok ] Stop the server\n");
    }

    private static OperationSnapshot StopStartStop(
        OperationStepStatus stop,
        OperationStepStatus start,
        OperationStepStatus stopAgain)
    {
        OperationStepStatus[] statuses = [stop, start, stopAgain];
        int current = Array.IndexOf(statuses, OperationStepStatus.Running);
        return new OperationSnapshot(
            Guid.Empty,
            "change-server-id",
            OperationStatus.Running,
            [
                new OperationStepState("stop-server", "Stop the server", stop, null),
                new OperationStepState("start-server", "Start the server", start, null),
                new OperationStepState("stop-server", "Stop the server", stopAgain, null),
            ],
            current >= 0 ? current : null,
            null,
            null);
    }

    private static OperationSnapshot Snapshot(
        OperationStepStatus download,
        OperationStepStatus upgrade,
        int? percent = null)
    {
        int? current = download == OperationStepStatus.Running ? 0
            : upgrade == OperationStepStatus.Running ? 1
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
