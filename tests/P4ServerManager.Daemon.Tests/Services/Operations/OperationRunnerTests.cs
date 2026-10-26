using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Hubs;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Tests.Services.Operations;

public sealed class OperationRunnerTests
{
    private static readonly Guid InstanceId = Guid.NewGuid();

    private readonly OperationTracker _tracker = new();
    private readonly List<OperationSnapshot> _pushed = [];
    private readonly OperationRunner _runner;

    public OperationRunnerTests()
    {
        IInstancesClient client = Substitute.For<IInstancesClient>();
        client.OperationProgress(Arg.Do<OperationSnapshot>(_pushed.Add)).Returns(Task.CompletedTask);
        IHubContext<InstancesHub, IInstancesClient> hubContext = Substitute.For<IHubContext<InstancesHub, IInstancesClient>>();
        hubContext.Clients.All.Returns(client);

        InstanceNotifier notifier = new(hubContext, new InstanceRuntimeRegistry(), NullLogger<InstanceNotifier>.Instance);
        _runner = new OperationRunner(_tracker, notifier);
    }

    [Fact]
    public async Task RunAsync_RunsTheStepsInOrderAndSkipsTheOnesThatDoNotApply()
    {
        List<string> ran = [];
        FakeStep first = new("first", ran);
        FakeStep skipped = new("skipped", ran) { Applies = false };
        FakeStep last = new("last", ran);

        await _runner.RunAsync(InstanceId, "test", [first, skipped, last], new object(), TestContext.Current.CancellationToken);

        ran.ShouldBe(["first", "last"]);
        _pushed[0].Steps.Select(s => s.Status).ShouldBe(
            [OperationStepStatus.Pending, OperationStepStatus.Skipped, OperationStepStatus.Pending]);
        OperationSnapshot snapshot = _tracker.Get(InstanceId)!;
        snapshot.Status.ShouldBe(OperationStatus.Succeeded);
        snapshot.Kind.ShouldBe("test");
        snapshot.Steps.Select(s => (s.Key, s.Status, s.Message)).ShouldBe(
        [
            ("first", OperationStepStatus.Succeeded, "first done"),
            ("skipped", OperationStepStatus.Skipped, null),
            ("last", OperationStepStatus.Succeeded, "last done"),
        ]);
    }

    [Fact]
    public async Task RunAsync_ReportsTheProgressOfTheCurrentStep()
    {
        FakeStep step = new("download", []) { Progress = new OperationStepProgress("Downloading (50%)", 50) };

        await _runner.RunAsync(InstanceId, "test", [step], new object(), TestContext.Current.CancellationToken);

        _pushed.ShouldContain(s => s.CurrentStep == "download"
            && s.Percent == 50
            && s.Steps[0].Message == "Downloading (50%)");
    }

    [Fact]
    public async Task RunAsync_StopsAtTheFailedStepAndRethrows()
    {
        List<string> ran = [];
        FakeStep failing = new("failing", ran) { Error = new InvalidOperationException("Boom") };
        FakeStep next = new("next", ran);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _runner.RunAsync(InstanceId, "test", [failing, next], new object(), TestContext.Current.CancellationToken));

        ran.ShouldBe(["failing"]);
        OperationSnapshot snapshot = _tracker.Get(InstanceId)!;
        snapshot.Status.ShouldBe(OperationStatus.Failed);
        snapshot.Error.ShouldBe("Boom");
        snapshot.Steps.Select(s => s.Status).ShouldBe([OperationStepStatus.Failed, OperationStepStatus.Pending]);
    }

    private sealed class FakeStep(string key, List<string> ran) : IOperationStep<object>
    {
        public string Key => key;

        public string Title => key;

        public bool Applies { get; init; } = true;

        public OperationStepProgress? Progress { get; init; }

        public Exception? Error { get; init; }

        public bool ShouldRun(object context)
        {
            return Applies;
        }

        public Task<string?> RunAsync(
            object context,
            IProgress<OperationStepProgress> progress,
            CancellationToken cancellationToken)
        {
            ran.Add(key);
            if (Progress is { } value)
            {
                progress.Report(value);
            }
            return Error is null ? Task.FromResult<string?>($"{key} done") : Task.FromException<string?>(Error);
        }
    }
}
