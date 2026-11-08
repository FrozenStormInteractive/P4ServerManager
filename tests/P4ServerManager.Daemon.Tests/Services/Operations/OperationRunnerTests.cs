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

        await _runner.RunAsync(InstanceId, "test", Plan(first, skipped, last), new object(), TestContext.Current.CancellationToken);

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
    public async Task RunAsync_LeavesOutTheStepsNotIncluded()
    {
        List<string> ran = [];
        FakeStep first = new("first", ran);
        FakeStep excluded = new("excluded", ran) { Included = false };
        FakeStep last = new("last", ran);

        await _runner.RunAsync(InstanceId, "test", Plan(first, excluded, last), new object(), TestContext.Current.CancellationToken);

        ran.ShouldBe(["first", "last"]);
        _pushed[0].Steps.Select(s => s.Key).ShouldBe(["first", "last"]);
        _tracker.Get(InstanceId)!.Steps.Select(s => (s.Key, s.Status)).ShouldBe(
        [
            ("first", OperationStepStatus.Succeeded),
            ("last", OperationStepStatus.Succeeded),
        ]);
    }

    [Fact]
    public async Task RunAsync_ReportsTheProgressOfTheCurrentStep()
    {
        FakeStep step = new("download", []) { Progress = new OperationStepProgress("Downloading (50%)", 50) };

        await _runner.RunAsync(InstanceId, "test", Plan(step), new object(), TestContext.Current.CancellationToken);

        _pushed.ShouldContain(s => s.CurrentStep == "download"
            && s.Percent == 50
            && s.Steps[0].Message == "Downloading (50%)");
    }

    [Fact]
    public async Task RunAsync_SkipsAStepThatAnEarlierOneMadeUnnecessary()
    {
        List<string> ran = [];
        FakeStep last = new("last", ran);
        FakeStep first = new("first", ran) { OnRun = () => last.Applies = false };

        await _runner.RunAsync(InstanceId, "test", Plan(first, last), new object(), TestContext.Current.CancellationToken);

        ran.ShouldBe(["first"]);
        _pushed[0].Steps.Select(s => s.Status).ShouldBe([OperationStepStatus.Pending, OperationStepStatus.Pending]);
        OperationSnapshot snapshot = _tracker.Get(InstanceId)!;
        snapshot.Status.ShouldBe(OperationStatus.Succeeded);
        snapshot.Steps.Select(s => s.Status).ShouldBe([OperationStepStatus.Succeeded, OperationStepStatus.Skipped]);
    }

    [Fact]
    public async Task RunAsync_GivesTheOutcomeOfTheContextOnceSucceeded()
    {
        OutcomeContext context = new();
        FakeStep step = new("step", []) { OnRun = () => context.Outcome = "up-to-date" };

        await _runner.RunAsync(InstanceId, "test", Plan(step), context, TestContext.Current.CancellationToken);

        _pushed.Where(s => s.Status == OperationStatus.Running).ShouldAllBe(s => s.Outcome == null);
        _tracker.Get(InstanceId)!.Outcome.ShouldBe("up-to-date");
    }

    [Fact]
    public async Task RunAsync_StopsAtTheFailedStepAndRethrows()
    {
        List<string> ran = [];
        FakeStep failing = new("failing", ran) { Error = new InvalidOperationException("Boom") };
        FakeStep next = new("next", ran);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _runner.RunAsync(InstanceId, "test", Plan(failing, next), new object(), TestContext.Current.CancellationToken));

        ran.ShouldBe(["failing"]);
        OperationSnapshot snapshot = _tracker.Get(InstanceId)!;
        snapshot.Status.ShouldBe(OperationStatus.Failed);
        snapshot.Error.ShouldBe("Boom");
        snapshot.Steps.Select(s => s.Status).ShouldBe([OperationStepStatus.Failed, OperationStepStatus.Pending]);
    }

    [Fact]
    public async Task RunAsync_LeavesOutTheStepsThePlanDoesNotInclude()
    {
        List<string> ran = [];
        OperationPlan<object> plan = new OperationPlan<object>()
            .Then(new FakeStep("first", ran))
            .ThenIf(false, new FakeStep("excluded", ran))
            .ThenIf(true, new FakeStep("last", ran));

        await _runner.RunAsync(InstanceId, "test", plan, new object(), TestContext.Current.CancellationToken);

        ran.ShouldBe(["first", "last"]);
        _tracker.Get(InstanceId)!.Steps.Select(s => s.Key).ShouldBe(["first", "last"]);
    }

    [Fact]
    public async Task RunAsync_SkipsTheStepsThePlanSkips()
    {
        List<string> ran = [];
        SkipContext context = new();
        OperationPlan<SkipContext> plan = new OperationPlan<SkipContext>()
            .Then(new FakeStep("first", ran) { OnRun = () => context.SkipLast = true })
            .Then(new FakeStep("skipped", ran), skipWhen: _ => true)
            .Then(new FakeStep("last", ran), skipWhen: c => c.SkipLast);

        await _runner.RunAsync(InstanceId, "test", plan, context, TestContext.Current.CancellationToken);

        ran.ShouldBe(["first"]);
        _pushed[0].Steps.Select(s => s.Status).ShouldBe(
            [OperationStepStatus.Pending, OperationStepStatus.Skipped, OperationStepStatus.Pending]);
        _tracker.Get(InstanceId)!.Steps.Select(s => s.Status).ShouldBe(
            [OperationStepStatus.Succeeded, OperationStepStatus.Skipped, OperationStepStatus.Skipped]);
    }

    [Fact]
    public async Task RunAsync_RunsTheOperationWithThePlanItGives()
    {
        List<string> ran = [];
        FakeOperation operation = new(new FakeStep("only", ran));

        await _runner.RunAsync(InstanceId, operation, new SkipContext(), TestContext.Current.CancellationToken);

        ran.ShouldBe(["only"]);
        _tracker.Get(InstanceId)!.Kind.ShouldBe("fake");
    }

    [Fact]
    public async Task RunAsync_SharesAGenericStepBetweenOperationsOfOtherContexts()
    {
        List<string> ran = [];
        FakeStep shared = new("shared", ran);

        await _runner.RunAsync(
            InstanceId,
            "first",
            new OperationPlan<SkipContext>().Then(shared),
            new SkipContext(),
            TestContext.Current.CancellationToken);
        await _runner.RunAsync(
            InstanceId,
            "second",
            new OperationPlan<OutcomeContext>().Then(shared),
            new OutcomeContext(),
            TestContext.Current.CancellationToken);

        ran.ShouldBe(["shared", "shared"]);
    }

    [Fact]
    public void Then_RefusesASecondStepWithTheSameKey()
    {
        OperationPlan<object> plan = new OperationPlan<object>().Then(new FakeStep("step", []));

        Should.Throw<InvalidOperationException>(() => plan.Then(new FakeStep("step", [])))
            .Message.ShouldContain("'step'");
    }

    private static OperationPlan<object> Plan(params IOperationStep<object>[] steps)
    {
        OperationPlan<object> plan = new();
        foreach (IOperationStep<object> step in steps)
        {
            plan.Then(step);
        }
        return plan;
    }

    private sealed class SkipContext
    {
        public bool SkipLast { get; set; }
    }

    private sealed class FakeOperation(IOperationStep<object> step) : IOperationDefinition<SkipContext>
    {
        public string Kind => "fake";

        public OperationPlan<SkipContext> Plan(SkipContext context)
        {
            return new OperationPlan<SkipContext>().Then(step);
        }
    }

    private sealed class OutcomeContext : IOperationOutcome
    {
        public string? Outcome { get; set; }
    }

    private sealed class FakeStep(string key, List<string> ran) : IOperationStep<object>
    {
        public string Key => key;

        public string Title => key;

        public bool Included { get; init; } = true;

        public bool Applies { get; set; } = true;

        public Action? OnRun { get; init; }

        public OperationStepProgress? Progress { get; init; }

        public Exception? Error { get; init; }

        public bool IsIncluded(object context)
        {
            return Included;
        }

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
            OnRun?.Invoke();
            if (Progress is { } value)
            {
                progress.Report(value);
            }
            return Error is null ? Task.FromResult<string?>($"{key} done") : Task.FromException<string?>(Error);
        }
    }
}
