using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.HealthChecks;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Tests.Api;

public sealed class HealthChecksApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly IP4ServerBuildCache _buildCache = Substitute.For<IP4ServerBuildCache>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _buildCache.GetP4dPath(Arg.Any<P4ServerBuild>()).Returns("/builds/p4d");
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, string.Empty, string.Empty));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Daemon:DataDirectory", _dataDirectory);
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(_buildCache));
                services.Replace(ServiceDescriptor.Singleton(_processRunner));
                services.Replace(ServiceDescriptor.Singleton(P4ServerPlatformDefinition.LinuxX64));
                services.RemoveAll<IHostedService>();
            });
        });
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Start_RunsTheChecksOnTheRootAndEndsHealthyWhenTheyFindNothing()
    {
        Guid id = await CreateInstanceAsync();

        HealthCheckRunDto run = await StartAndWaitAsync(id);

        run.Status.ShouldBe(HealthCheckStatus.Healthy);
        run.Checks.ShouldBe([HealthCheckKind.Validate, HealthCheckKind.CrossCheck]);
        run.P4dVersion.ShouldBe("2026.1/3089746");
        run.FinishedAt.ShouldNotBeNull();
        run.Progress.ShouldBeNull();
        run.Findings.ShouldBeEmpty();

        List<ProcessCommand> commands = Commands();
        commands.Select(c => c.FileName).ShouldBe(["/builds/p4d", "/builds/p4d"]);
        commands[0].Arguments.ShouldBe(["-r", "/srv/main/root", "-xv"]);

        // Never the jnl.fix of the root: it may be one that an administrator kept.
        commands[1].Arguments.Take(4).ShouldBe(["-r", "/srv/main/root", "-xx", "-o"]);
        commands[1].Arguments[4].ShouldStartWith(_dataDirectory);
    }

    [Fact]
    public async Task Start_RunsOnlyTheChecksAskedFor_InTheirOrder()
    {
        Guid id = await CreateInstanceAsync();

        HealthCheckRunDto run = await StartAndWaitAsync(id, HealthCheckKind.CrossCheck);

        run.Checks.ShouldBe([HealthCheckKind.CrossCheck]);
        Commands().ShouldHaveSingleItem().Arguments.ShouldContain("-xx");
    }

    [Fact]
    public async Task Start_KeepsWhatTheChecksFind()
    {
        Guid id = await CreateInstanceAsync();
        StubCheck("-xv", 127,
            output: ["Validating db.config", "Validating db.rev", "\t**** Checksum mismatch **** possible data corruption"],
            errors: ["Perforce server error:", "\tProblems were found in 1 table(s)."]);
        StubCheck("-xx", 2,
            output: ["Check db.change/db.desc..."],
            errors:
            [
                "Perforce server error:",
                "\tdb.desc not found in default/specified server root",
                "Perforce server error:",
                "\tdb.change/db.desc inconsistencies found.",
            ]);

        HealthCheckRunDto run = await StartAndWaitAsync(id);

        run.Status.ShouldBe(HealthCheckStatus.ProblemsFound);
        run.Error.ShouldBeNull();
        HealthCheckFindingDto[] expected =
        [
            new(HealthCheckKind.Validate, "db.rev", "**** Checksum mismatch **** possible data corruption"),
            new(HealthCheckKind.CrossCheck, "db.desc", "db.desc not found in default/specified server root"),
            new(HealthCheckKind.CrossCheck, "db.change/db.desc", "db.change/db.desc inconsistencies found."),
        ];
        run.Findings.ShouldBe(expected);

        // The report is read back from the database.
        HealthCheckRunDto[] reports = await GetRunsAsync(id);
        reports.ShouldHaveSingleItem().Findings.ShouldBe(expected);
    }

    [Fact]
    public async Task Start_FailsWithoutFindingsWhenP4dCannotDoItsWork()
    {
        Guid id = await CreateInstanceAsync();
        StubCheck("-xv", 1,
            output: [],
            errors: ["Perforce server error:", "\tRoot directory (set with $P4ROOT or -r flag) invalid."]);

        HealthCheckRunDto run = await StartAndWaitAsync(id, HealthCheckKind.Validate);

        run.Status.ShouldBe(HealthCheckStatus.Failed);
        run.Error.ShouldBe("Root directory (set with $P4ROOT or -r flag) invalid.");
        run.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_ReportsAP4dThatDoesNotStart()
    {
        Guid id = await CreateInstanceAsync();
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns<ProcessResult>(_ => throw new InvalidOperationException("Could not start p4d."));

        HealthCheckRunDto run = await StartAndWaitAsync(id);

        run.Status.ShouldBe(HealthCheckStatus.Failed);
        run.Error.ShouldBe("Could not start p4d.");
    }

    [Fact]
    public async Task Start_RefusesAnEmptyListOfChecks()
    {
        Guid id = await CreateInstanceAsync();

        HttpResponseMessage response = await PostChecksAsync(id);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_ReturnsNotFoundForAnUnknownInstance()
    {
        HttpResponseMessage response = await PostChecksAsync(Guid.NewGuid(), HealthCheckKind.Validate);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Start_RefusesAnInstanceThatIsNotReady()
    {
        Guid id = await CreateInstanceAsync(P4ServerInstanceState.Failed);

        HttpResponseMessage response = await PostChecksAsync(id, HealthCheckKind.Validate);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task WhileItRuns_TellsItsProgressAndBlocksWhatWouldChangeTheDatabase()
    {
        Guid id = await CreateInstanceAsync();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                ProcessCommand command = call.Arg<ProcessCommand>();
                await command.OnOutputLine!("Validating db.rev");
                await command.OnOutputLine!("\t**** Checksum mismatch ****");
                reached.TrySetResult();
                await release.Task.WaitAsync(call.Arg<CancellationToken>());
                return new ProcessResult(0, string.Empty, string.Empty);
            });

        Guid runId = await StartAsync(id, HealthCheckKind.Validate);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        HealthCheckRunDto running = (await GetRunsAsync(id)).ShouldHaveSingleItem();
        running.Status.ShouldBe(HealthCheckStatus.Running);
        running.Progress.ShouldNotBeNull();
        running.Progress.Check.ShouldBe(HealthCheckKind.Validate);
        running.Progress.Item.ShouldBe("db.rev");
        running.Progress.ItemsChecked.ShouldBe(1);
        running.Findings.ShouldHaveSingleItem().Table.ShouldBe("db.rev");

        // Reading it again gives the same run, with its progress.
        (await GetRunAsync(id, runId)).Progress.ShouldNotBeNull();

        // Nothing starts p4d or changes the database until the end of the check.
        (await GetInstanceStatusAsync(id)).ShouldBe(InstanceRuntimeStatus.CheckingHealth);
        foreach (string action in new[] { "start", "stop", "restart" })
        {
            (await _client.PostAsync($"/api/instances/{id}/{action}", null, TestContext.Current.CancellationToken))
                .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        (await PostChecksAsync(id, HealthCheckKind.Validate)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DeleteRunAsync(id, runId)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _client.DeleteAsync($"/api/instances/{id}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _client.PostAsJsonAsync(
                $"/api/instances/{id}/convert-to-unicode",
                new { },
                TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        release.SetResult();
        (await WaitForEndAsync(id, runId)).Status.ShouldBe(HealthCheckStatus.ProblemsFound);
        (await GetInstanceStatusAsync(id)).ShouldBe(InstanceRuntimeStatus.Stopped);
    }

    [Fact]
    public async Task Cancel_KillsTheCheckAndKeepsWhatItFound()
    {
        Guid id = await CreateInstanceAsync();
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.Arg<ProcessCommand>().OnOutputLine!("Validating db.rev");
                await call.Arg<ProcessCommand>().OnOutputLine!("\t**** Checksum mismatch ****");
                reached.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return new ProcessResult(0, string.Empty, string.Empty);
            });

        Guid runId = await StartAsync(id);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        (await CancelAsync(id, runId)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        HealthCheckRunDto run = await WaitForEndAsync(id, runId);
        run.Status.ShouldBe(HealthCheckStatus.Canceled);
        run.Findings.ShouldHaveSingleItem();

        // The cross-check does not run.
        Commands().ShouldHaveSingleItem();
        (await CancelAsync(id, runId)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cancel_DropsTheLinesOfP4dThatComeAfterTheEnd()
    {
        Guid id = await CreateInstanceAsync();
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource lateLine = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                ProcessCommand command = call.Arg<ProcessCommand>();
                reached.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                }
                catch (OperationCanceledException)
                {
                    // The real runner does not wait for its readers once it kills p4d.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(200, CancellationToken.None);
                        await command.OnOutputLine!("Validating db.rev");
                        await command.OnOutputLine!("\t**** Checksum mismatch ****");
                        lateLine.TrySetResult();
                    }, CancellationToken.None);
                    throw;
                }
                return new ProcessResult(0, string.Empty, string.Empty);
            });

        Guid runId = await StartAsync(id, HealthCheckKind.Validate);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        (await CancelAsync(id, runId)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitForEndAsync(id, runId);
        await lateLine.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        HealthCheckRunDto run = await GetRunAsync(id, runId);
        run.Status.ShouldBe(HealthCheckStatus.Canceled);
        run.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task FixJournal_IsKeptOnlyWhenTheCrossCheckFoundSomething()
    {
        Guid id = await CreateInstanceAsync();
        _processRunner.RunAsync(
                Arg.Is<ProcessCommand>(c => c.Arguments.Contains("-xx")),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                ProcessCommand command = call.Arg<ProcessCommand>();
                await File.WriteAllTextAsync(command.Arguments[^1], "@pv@ 1 @db.desc@ 1 @init@\n");
                return new ProcessResult(0, string.Empty, string.Empty);
            });

        HealthCheckRunDto healthy = await StartAndWaitAsync(id, HealthCheckKind.CrossCheck);

        healthy.HasFixJournal.ShouldBeFalse();
        string healthyPath = _factory.Services.GetRequiredService<DaemonPaths>()
            .GetHealthCheckFixJournalPath(id, healthy.Id);
        File.Exists(healthyPath).ShouldBeFalse();
        (await GetFixJournalAsync(id, healthy.Id)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        _processRunner.RunAsync(
                Arg.Is<ProcessCommand>(c => c.Arguments.Contains("-xx")),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                ProcessCommand command = call.Arg<ProcessCommand>();
                await File.WriteAllTextAsync(command.Arguments[^1], "@pv@ 1 @db.desc@ 1 @init@\n");
                await command.OnErrorLine!("Perforce server error:");
                await command.OnErrorLine!("\tdb.change/db.desc inconsistencies found.");
                return new ProcessResult(1, string.Empty, string.Empty);
            });

        HealthCheckRunDto unhealthy = await StartAndWaitAsync(id, HealthCheckKind.CrossCheck);

        unhealthy.HasFixJournal.ShouldBeTrue();
        HttpResponseMessage journal = await GetFixJournalAsync(id, unhealthy.Id);
        journal.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await journal.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("@db.desc@");
        journal.Dispose();

        // It stays with the report that tells the health of the instance, even once that report is deleted.
        string unhealthyPath = _factory.Services.GetRequiredService<DaemonPaths>()
            .GetHealthCheckFixJournalPath(id, unhealthy.Id);
        (await DeleteRunAsync(id, unhealthy.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(unhealthyPath).ShouldBeTrue();
        using (HttpResponseMessage hiddenJournal = await GetFixJournalAsync(id, unhealthy.Id))
        {
            hiddenJournal.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // The report leaves with its journal, once a later check replaces it.
        _processRunner.RunAsync(
                Arg.Is<ProcessCommand>(c => c.Arguments.Contains("-xx")),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, string.Empty, string.Empty));
        await StartAndWaitAsync(id, HealthCheckKind.CrossCheck);

        File.Exists(unhealthyPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_RemovesAReportThatNoLongerTellsTheHealth()
    {
        Guid id = await CreateInstanceAsync();
        HealthCheckRunDto first = await StartAndWaitAsync(id);
        HealthCheckRunDto second = await StartAndWaitAsync(id);

        // The latest comes first.
        (await GetRunsAsync(id)).Select(r => r.Id).ShouldBe([second.Id, first.Id]);

        (await DeleteRunAsync(id, first.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await GetRunsAsync(id)).ShouldHaveSingleItem().Id.ShouldBe(second.Id);
        (await DeleteRunAsync(id, first.Id)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_RemovesAReportOfACheckThatFailed()
    {
        Guid id = await CreateInstanceAsync();
        StubCheck("-xv", 1, output: [], errors: ["Perforce server error:", "\tRoot directory invalid."]);
        HealthCheckRunDto failed = await StartAndWaitAsync(id, HealthCheckKind.Validate);

        (await DeleteRunAsync(id, failed.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await GetRunsAsync(id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_OnlyHidesTheReportThatTellsTheHealth()
    {
        Guid id = await CreateInstanceAsync();
        StubCheck("-xv", 127, output: ["Validating db.rev", "\tbroken"], errors: []);
        HealthCheckRunDto run = await StartAndWaitAsync(id, HealthCheckKind.Validate);

        (await DeleteRunAsync(id, run.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HealthCheckRunDto hidden = (await GetRunsAsync(id)).ShouldHaveSingleItem();
        hidden.ReportHidden.ShouldBeTrue();
        hidden.Status.ShouldBe(HealthCheckStatus.ProblemsFound);
        hidden.Findings.ShouldHaveSingleItem();

        // It is deleted, as far as anyone asks.
        (await DeleteRunAsync(id, run.Id)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HiddenReport_IsDeletedOnceALaterCheckReplacesIt()
    {
        Guid id = await CreateInstanceAsync();
        HealthCheckRunDto run = await StartAndWaitAsync(id);
        (await DeleteRunAsync(id, run.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HealthCheckRunDto later = await StartAndWaitAsync(id);

        (await GetRunsAsync(id)).ShouldHaveSingleItem().Id.ShouldBe(later.Id);
    }

    [Fact]
    public async Task HiddenReport_StaysUntilEachOfItsChecksIsReplaced()
    {
        Guid id = await CreateInstanceAsync();
        HealthCheckRunDto run = await StartAndWaitAsync(id);
        (await DeleteRunAsync(id, run.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // It still tells the consistency of the tables.
        HealthCheckRunDto validation = await StartAndWaitAsync(id, HealthCheckKind.Validate);
        (await GetRunsAsync(id)).Select(r => r.Id).ShouldBe([validation.Id, run.Id]);

        HealthCheckRunDto crossCheck = await StartAndWaitAsync(id, HealthCheckKind.CrossCheck);
        (await GetRunsAsync(id)).Select(r => r.Id).ShouldBe([crossCheck.Id, validation.Id]);
    }

    [Fact]
    public async Task HiddenReport_StaysWhenALaterCheckDoesNotRunToItsEnd()
    {
        Guid id = await CreateInstanceAsync();
        HealthCheckRunDto run = await StartAndWaitAsync(id);
        (await DeleteRunAsync(id, run.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        StubCheck("-xv", 1, output: [], errors: ["Perforce server error:", "\tRoot directory invalid."]);

        HealthCheckRunDto failed = await StartAndWaitAsync(id, HealthCheckKind.Validate);

        failed.Status.ShouldBe(HealthCheckStatus.Failed);
        (await GetRunsAsync(id)).Select(r => r.Id).ShouldBe([failed.Id, run.Id]);
    }

    [Fact]
    public async Task DeletingTheInstance_RemovesItsReports()
    {
        Guid id = await CreateInstanceAsync();
        StubCheck("-xv", 127, output: ["Validating db.rev", "\tbroken"], errors: []);
        await StartAndWaitAsync(id, HealthCheckKind.Validate);

        (await _client.DeleteAsync($"/api/instances/{id}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using DaemonDbContext db = await CreateDbContextAsync();
        (await db.HealthCheckRuns.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.HealthCheckFindings.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task List_ReturnsNotFoundForAnUnknownInstance()
    {
        HttpResponseMessage response = await _client.GetAsync(
            $"/api/instances/{Guid.NewGuid()}/health-checks",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CrossCheck_ScalesItsProgressWithTheLatestOneThatRanToItsEnd()
    {
        Guid id = await CreateInstanceAsync();

        // No earlier check tells the number of pairs: 2 of the estimated 27.
        (await GetPercentAtPairAsync(id, reachedPairs: 2, totalPairs: 8)).ShouldBe(7);

        // 2 of the 8 the first one reached.
        (await GetPercentAtPairAsync(id, reachedPairs: 2, totalPairs: 4)).ShouldBe(25);

        // 2 of the 4 the second one reached.
        (await GetPercentAtPairAsync(id, reachedPairs: 2, totalPairs: 4)).ShouldBe(50);
    }

    [Fact]
    public async Task CrossCheck_CountsThePairsOfOneThatFoundProblems()
    {
        Guid id = await CreateInstanceAsync();
        StubCheck("-xx", 1,
            output: ["Check db.change/db.desc...", "Check db.rev/db.change..."],
            errors: ["Perforce server error:", "\tdb.change/db.desc inconsistencies found."]);
        (await StartAndWaitAsync(id, HealthCheckKind.CrossCheck)).Status.ShouldBe(HealthCheckStatus.ProblemsFound);

        (await GetPercentAtPairAsync(id, reachedPairs: 1, totalPairs: 1)).ShouldBe(50);
    }

    [Fact]
    public async Task CrossCheck_IgnoresThePairsOfOneThatDidNotRunToItsEnd()
    {
        Guid id = await CreateInstanceAsync();
        StubCheck("-xx", 1,
            output: ["Check db.change/db.desc..."],
            errors: ["Perforce server error:", "\tRoot directory (set with $P4ROOT or -r flag) invalid."]);
        (await StartAndWaitAsync(id, HealthCheckKind.CrossCheck)).Status.ShouldBe(HealthCheckStatus.Failed);

        (await GetPercentAtPairAsync(id, reachedPairs: 2, totalPairs: 2)).ShouldBe(7);
    }

    [Fact]
    public async Task CrossCheck_IgnoresThePairsOfOneThatWasCanceled()
    {
        Guid id = await CreateInstanceAsync();
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.Arg<ProcessCommand>().OnOutputLine!("Check db.change/db.desc...");
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return new ProcessResult(0, string.Empty, string.Empty);
            });
        Guid runId = await StartAsync(id, HealthCheckKind.CrossCheck);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        (await CancelAsync(id, runId)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await WaitForEndAsync(id, runId)).Status.ShouldBe(HealthCheckStatus.Canceled);

        (await GetPercentAtPairAsync(id, reachedPairs: 2, totalPairs: 2)).ShouldBe(7);
    }

    [Fact]
    public async Task CrossCheck_IgnoresThePairsOfAnotherBuild()
    {
        Guid id = await CreateInstanceAsync();
        await GetPercentAtPairAsync(id, reachedPairs: 2, totalPairs: 4);
        await using (DaemonDbContext db = await CreateDbContextAsync())
        {
            await db.HealthCheckRuns.ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.P4dVersion, "2025.2/2900000"),
                TestContext.Current.CancellationToken);
        }

        (await GetPercentAtPairAsync(id, reachedPairs: 2, totalPairs: 4)).ShouldBe(7);
    }

    [Fact]
    public async Task EndLeftRuns_EndsTheRunsOfAPreviousDaemonAndTellsTheServersTheyLeftStopped()
    {
        Guid id = await CreateInstanceAsync();
        Guid runId = Guid.NewGuid();
        await using (DaemonDbContext db = await CreateDbContextAsync())
        {
            db.HealthCheckRuns.Add(new HealthCheckRun
            {
                Id = runId,
                InstanceId = id,
                Status = HealthCheckStatus.Running,
                Checks = [HealthCheckKind.Validate],
                P4dVersion = "2026.1/3089746",
                StartedAt = DateTimeOffset.UtcNow,
                ServerRestartPending = true,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        HealthCheckRunner runner = _factory.Services.GetRequiredService<HealthCheckRunner>();
        (await runner.EndLeftRunsAsync(TestContext.Current.CancellationToken)).ShouldBe([id]);

        // Started once only.
        (await runner.EndLeftRunsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();

        HealthCheckRunDto run = await GetRunAsync(id, runId);
        run.Status.ShouldBe(HealthCheckStatus.Interrupted);
        run.FinishedAt.ShouldNotBeNull();
    }

    private void StubCheck(string flag, int exitCode, string[] output, string[] errors)
    {
        _processRunner.RunAsync(
                Arg.Is<ProcessCommand>(c => c.Arguments.Contains(flag)),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                ProcessCommand command = call.Arg<ProcessCommand>();
                foreach (string line in output)
                {
                    await command.OnOutputLine!(line);
                }
                foreach (string line in errors)
                {
                    await command.OnErrorLine!(line);
                }
                return new ProcessResult(exitCode, string.Empty, string.Empty);
            });
    }

    /// <summary>
    /// Runs a cross check to its end, and reads its progress when it has reached some of its pairs of tables.
    /// </summary>
    private async Task<int> GetPercentAtPairAsync(Guid id, int reachedPairs, int totalPairs)
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _processRunner.RunAsync(Arg.Any<ProcessCommand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                ProcessCommand command = call.Arg<ProcessCommand>();
                for (int pair = 1; pair <= totalPairs; pair++)
                {
                    await command.OnOutputLine!($"Check db.a{pair}/db.b{pair}...");
                    if (pair == reachedPairs)
                    {
                        reached.TrySetResult();
                        await release.Task.WaitAsync(call.Arg<CancellationToken>());
                    }
                }
                return new ProcessResult(0, string.Empty, string.Empty);
            });

        Guid runId = await StartAsync(id, HealthCheckKind.CrossCheck);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        int percent = (await GetRunAsync(id, runId)).Progress!.Percent;

        release.SetResult();
        (await WaitForEndAsync(id, runId)).Status.ShouldBe(HealthCheckStatus.Healthy);
        return percent;
    }

    private List<ProcessCommand> Commands()
    {
        return _processRunner.ReceivedCalls().Select(call => (ProcessCommand)call.GetArguments()[0]!).ToList();
    }

    private Task<HttpResponseMessage> PostChecksAsync(Guid id, params HealthCheckKind[] checks)
    {
        return _client.PostAsJsonAsync(
            $"/api/instances/{id}/health-checks",
            new { checks = checks.Select(c => c.ToString()) },
            TestContext.Current.CancellationToken);
    }

    private async Task<Guid> StartAsync(Guid id, params HealthCheckKind[] checks)
    {
        HttpResponseMessage response = await PostChecksAsync(
            id,
            checks.Length > 0 ? checks : [HealthCheckKind.Validate, HealthCheckKind.CrossCheck]);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        HealthCheckRunDto? run = await response.Content.ReadFromJsonAsync<HealthCheckRunDto>(
            JsonOptions,
            TestContext.Current.CancellationToken);
        return run!.Id;
    }

    private async Task<HealthCheckRunDto> StartAndWaitAsync(Guid id, params HealthCheckKind[] checks)
    {
        return await WaitForEndAsync(id, await StartAsync(id, checks));
    }

    private async Task<HealthCheckRunDto> WaitForEndAsync(Guid id, Guid runId)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            HealthCheckRunDto run = await GetRunAsync(id, runId);
            if (run.Status != HealthCheckStatus.Running)
            {
                // The instance is released once p4d is started again, if it ran.
                while (await GetInstanceStatusAsync(id) == InstanceRuntimeStatus.CheckingHealth)
                {
                    await Task.Delay(10, timeout.Token);
                }
                return run;
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    private async Task<InstanceRuntimeStatus> GetInstanceStatusAsync(Guid id)
    {
        InstanceDto? instance = await _client.GetFromJsonAsync<InstanceDto>(
            $"/api/instances/{id}",
            JsonOptions,
            TestContext.Current.CancellationToken);
        return instance!.RuntimeStatus;
    }

    private async Task<HealthCheckRunDto> GetRunAsync(Guid id, Guid runId)
    {
        return (await _client.GetFromJsonAsync<HealthCheckRunDto>(
            $"/api/instances/{id}/health-checks/{runId}",
            JsonOptions,
            TestContext.Current.CancellationToken))!;
    }

    private async Task<HealthCheckRunDto[]> GetRunsAsync(Guid id)
    {
        return (await _client.GetFromJsonAsync<HealthCheckRunDto[]>(
            $"/api/instances/{id}/health-checks",
            JsonOptions,
            TestContext.Current.CancellationToken))!;
    }

    private Task<HttpResponseMessage> CancelAsync(Guid id, Guid runId)
    {
        return _client.PostAsync(
            $"/api/instances/{id}/health-checks/{runId}/cancel",
            null,
            TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> DeleteRunAsync(Guid id, Guid runId)
    {
        return _client.DeleteAsync(
            $"/api/instances/{id}/health-checks/{runId}",
            TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> GetFixJournalAsync(Guid id, Guid runId)
    {
        return _client.GetAsync(
            $"/api/instances/{id}/health-checks/{runId}/fix-journal",
            TestContext.Current.CancellationToken);
    }

    private ValueTask<DaemonDbContext> CreateDbContextAsync()
    {
        return new ValueTask<DaemonDbContext>(_factory.Services
            .GetRequiredService<IDbContextFactory<DaemonDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken));
    }

    private async Task<Guid> CreateInstanceAsync(P4ServerInstanceState state = P4ServerInstanceState.Ready)
    {
        // The factory creates the database when it starts.
        _ = _factory.Server;

        await using DaemonDbContext db = await CreateDbContextAsync();
        P4ServerBuild build = new()
        {
            Id = Guid.NewGuid(),
            Release = "r26.1",
            Version = "2026.1",
            BuildNumber = "3089746",
            Platform = P4ServerPlatformDefinition.LinuxX64.Name,
            Sha256 = "a",
            DownloadedAt = DateTimeOffset.UtcNow,
            IsExtracted = true,
        };
        P4ServerInstance instance = new()
        {
            Id = Guid.NewGuid(),
            Name = "main",
            Port = 16660,
            RootPath = "/srv/main/root",
            Release = "r26.1",
            Build = build,
            CaseSensitive = true,
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Instances.Add(instance);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _factory.Services.GetRequiredService<InstanceRuntimeRegistry>().Set(instance.Id, InstanceRuntimeStatus.Stopped);
        return instance.Id;
    }
}
