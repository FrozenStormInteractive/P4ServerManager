using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.HealthChecks;

/// <summary>
/// Runs the health checks of the instances, at most one per instance. p4d is stopped first, so that the tables do not
/// change during the check and no user waits on them, then started again if it ran. The checks run <c>p4d -xv</c> and
/// <c>p4d -xx</c> on the root. Neither changes the database, so a check can be killed at any time. The run and what it
/// finds are saved as they come, and pushed to the clients with its progress. The <see cref="InstanceSupervisor"/> holds
/// the instance meanwhile.
/// </summary>
public sealed class HealthCheckRunner(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    P4ServerLauncher launcher,
    InstanceNotifier notifier,
    DaemonPaths paths,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    ILogger<HealthCheckRunner> logger)
{
    /// <summary>
    /// What is kept of a run, so that a database in a bad state cannot fill the database of the daemon.
    /// </summary>
    public const int MaxFindings = 500;

    /// <summary>
    /// Pairs of tables <c>p4d -xx</c> checks in 2026.1. It tells no number beforehand: this scales the progress until a
    /// check of the instance on its build has run to its end.
    /// </summary>
    private const int EstimatedPairs = 27;

    private readonly ConcurrentDictionary<Guid, ActiveRun> activeRuns = new();

    /// <summary>
    /// The state of a run in progress, which only the runner knows: its progress.
    /// </summary>
    /// <returns>The run, or null when it no longer runs.</returns>
    public HealthCheckRunDto? GetActive(Guid instanceId, Guid runId)
    {
        return activeRuns.TryGetValue(instanceId, out ActiveRun? active) && active.Snapshot.Id == runId
            ? active.Snapshot
            : null;
    }

    /// <summary>
    /// Saves a new run. The caller holds the instance, then runs it with <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    /// <param name="checks">The checks. They run in the order of <see cref="HealthCheckKind"/>.</param>
    /// <param name="cancellationToken">Aborts the request, until the run has begun.</param>
    public async Task<HealthCheckRunDto> BeginAsync(
        P4ServerInstance instance,
        IEnumerable<HealthCheckKind> checks,
        CancellationToken cancellationToken)
    {
        P4ServerBuild build = instance.Build
            ?? throw new InstanceConflictException($"Instance {instance.Name} has no p4d build yet.");

        HealthCheckRun run = new()
        {
            Id = Guid.NewGuid(),
            InstanceId = instance.Id,
            Status = HealthCheckStatus.Running,
            Checks = checks.Distinct().Order().ToList(),
            P4dVersion = $"{build.Version}/{build.BuildNumber}",
            StartedAt = timeProvider.GetUtcNow(),
            ServerRestartPending = launcher.IsRunning(instance),
        };
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            db.HealthCheckRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
        }

        Target target = new(
            instance.Name,
            buildCache.GetP4dPath(build),
            P4ServerLauncher.GetRootArguments(instance),
            instance.RootPath,
            paths.GetHealthCheckFixJournalPath(instance.Id, run.Id));
        ActiveRun active = new(
            run.ToDto(),
            target,
            run.ServerRestartPending,
            CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping));
        if (!activeRuns.TryAdd(instance.Id, active))
        {
            throw new InstanceConflictException($"A health check already runs on instance {instance.Name}.");
        }

        await notifier.HealthCheckChangedAsync(active.Snapshot);
        logger.LogInformation("Checking the health of instance {Name}", instance.Name);
        return active.Snapshot;
    }

    /// <summary>
    /// Stops p4d, runs the checks of the run <see cref="BeginAsync"/> saved, then starts p4d again if it ran, whether the
    /// checks found problems, failed or were canceled. A failure of that start is kept as the last error of the instance,
    /// which is not saved: the caller saves it.
    /// </summary>
    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    /// <param name="stoppingToken">The shutdown of the daemon: p4d is then started again by the next daemon.</param>
    public async Task RunAsync(P4ServerInstance instance, CancellationToken stoppingToken)
    {
        ActiveRun active = activeRuns[instance.Id];
        Guid runId = active.Snapshot.Id;
        await RunChecksAsync(active, instance);

        if (active.RestartServer && !stoppingToken.IsCancellationRequested)
        {
            await StartServerAgainAsync(instance, runId, stoppingToken);
        }
    }

    /// <summary>
    /// Asks a run to stop. It ends shortly after, as <see cref="HealthCheckStatus.Canceled"/>.
    /// </summary>
    /// <returns>Whether the run was still running.</returns>
    public async Task<bool> CancelAsync(Guid instanceId, Guid runId)
    {
        if (!activeRuns.TryGetValue(instanceId, out ActiveRun? active) || active.Snapshot.Id != runId)
        {
            return false;
        }

        try
        {
            await active.Cancellation.CancelAsync();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // It ended in the meantime.
            return false;
        }
    }

    /// <summary>
    /// Deletes a report that ended. The health of an instance rests on the latest conclusive report of each check: such a
    /// report is only hidden, and is deleted once a later check replaces it. Any other is deleted at once, with its fix
    /// journal.
    /// </summary>
    /// <returns>Whether the instance has such a report that was not deleted yet.</returns>
    public async Task<bool> DeleteReportAsync(Guid instanceId, Guid runId, CancellationToken cancellationToken)
    {
        HealthCheckRun? run;
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            List<HealthCheckRun> runs = await db.HealthCheckRuns
                .AsNoTracking()
                .Where(r => r.InstanceId == instanceId)
                .ToListAsync(cancellationToken);
            run = runs.FirstOrDefault(r => r.Id == runId && !r.ReportHidden);
            if (run is null)
            {
                return false;
            }

            if (FindRunsTellingTheHealth(runs).Contains(runId))
            {
                await db.HealthCheckRuns
                    .Where(r => r.Id == runId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.ReportHidden, true), cancellationToken);
                HealthCheckRun hidden = await db.HealthCheckRuns
                    .AsNoTracking()
                    .Include(r => r.Findings.OrderBy(f => f.Id))
                    .FirstAsync(r => r.Id == runId, cancellationToken);
                await notifier.HealthCheckChangedAsync(hidden.ToDto());
                return true;
            }
        }

        await RemoveReportAsync(instanceId, runId);
        return true;
    }

    /// <summary>
    /// Ends the runs left by a previous daemon process, as nothing reads their output any more, and tells which servers
    /// they left stopped. The caller starts them again.
    /// </summary>
    /// <returns>The instances whose p4d a check stopped, and that were not started again.</returns>
    public async Task<IReadOnlyList<Guid>> EndLeftRunsAsync(CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await db.HealthCheckRuns
            .Where(r => r.Status == HealthCheckStatus.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Status, HealthCheckStatus.Interrupted)
                    .SetProperty(r => r.FinishedAt, now),
                cancellationToken);

        List<Guid> instanceIds = await db.HealthCheckRuns
            .Where(r => r.ServerRestartPending)
            .Select(r => r.InstanceId)
            .Distinct()
            .ToListAsync(cancellationToken);
        await db.HealthCheckRuns
            .Where(r => r.ServerRestartPending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.ServerRestartPending, false), cancellationToken);
        return instanceIds;
    }

    private async Task StartServerAgainAsync(P4ServerInstance instance, Guid runId, CancellationToken stoppingToken)
    {
        try
        {
            // It still runs when it could not be stopped.
            if (!launcher.IsRunning(instance))
            {
                await launcher.StartAsync(instance, stoppingToken);
                logger.LogInformation("Started instance {Name} again after its health check", instance.Name);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not start instance {Name} again", instance.Name);
            instance.LastError = $"It could not be started again after its health check: {exception.Message}";
        }

        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await db.HealthCheckRuns
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.ServerRestartPending, false),
                CancellationToken.None);
    }

    private async Task RunChecksAsync(ActiveRun active, P4ServerInstance instance)
    {
        Target target = active.Target;
        HealthCheckStatus? status = null;
        List<string> errors = [];
        try
        {
            await launcher.StopAsync(instance, active.Cancellation.Token);
            Directory.CreateDirectory(Path.GetDirectoryName(target.FixJournalPath)!);
            IReadOnlyList<HealthCheckKind> checks = active.Snapshot.Checks;
            for (int index = 0; index < checks.Count; index++)
            {
                errors.AddRange(await RunCheckAsync(active, target, checks[index], index));
            }
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            status = lifetime.ApplicationStopping.IsCancellationRequested
                ? HealthCheckStatus.Interrupted
                : HealthCheckStatus.Canceled;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The health check of instance {Name} failed", target.InstanceName);
            errors.Add(exception.Message);
        }

        await FinishAsync(active, target, status, errors);
    }

    /// <returns>Why the check could not run to its end, if it could not.</returns>
    private async Task<IReadOnlyList<string>> RunCheckAsync(
        ActiveRun active,
        Target target,
        HealthCheckKind check,
        int index)
    {
        HealthCheckOutputParser parser = new();
        int estimatedItems = check == HealthCheckKind.Validate
            ? CountTables(target.RootPath)
            : await EstimatePairsAsync(active.Snapshot, active.Cancellation.Token);
        await ReportProgressAsync(active, parser, check, index, estimatedItems);

        IReadOnlyList<string> arguments = check == HealthCheckKind.Validate
            ? [.. target.RootArguments, "-xv"]
            : [.. target.RootArguments, "-xx", "-o", target.FixJournalPath];

        // Both outputs are read at the same time, and a check of a large database runs for hours.
        ProcessCommand command = new(
            target.P4dPath,
            arguments,
            Timeout: Timeout.InfiniteTimeSpan,
            OnOutputLine: line => active.WithGateAsync(async () =>
            {
                int itemsBefore = parser.ItemsChecked;
                await AddFindingAsync(active, check, parser.ReadOutputLine(line));
                if (parser.ItemsChecked != itemsBefore)
                {
                    await ReportProgressAsync(active, parser, check, index, estimatedItems);
                }
            }),
            OnErrorLine: line => active.WithGateAsync(
                () => AddFindingAsync(active, check, parser.ReadErrorLine(line))));

        ProcessResult result = await processRunner.RunAsync(command, active.Cancellation.Token);
        await active.WithGateAsync(() => AddFindingAsync(active, check, parser.Complete()));

        // p4d exits with the number of errors, which the findings usually tell in full.
        bool hasFindings = active.Snapshot.Findings.Any(f => f.Check == check);
        if (check == HealthCheckKind.CrossCheck && parser.Errors.Count == 0 && (result.Succeeded || hasFindings))
        {
            active.CrossCheckPairs = parser.ItemsChecked;
        }

        if (result.Succeeded)
        {
            return [];
        }
        if (parser.Errors.Count > 0)
        {
            return parser.Errors;
        }
        return hasFindings
            ? []
            : [$"p4d exited with code {result.ExitCode.ToString(CultureInfo.InvariantCulture)} without telling why."];
    }

    /// <summary>
    /// The pairs of tables the latest check of the instance reached on this build, when one ran to its end.
    /// </summary>
    private async Task<int> EstimatePairsAsync(HealthCheckRunDto run, CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var earlierRuns = await db.HealthCheckRuns
            .AsNoTracking()
            .Where(r => r.InstanceId == run.InstanceId && r.P4dVersion == run.P4dVersion && r.CrossCheckPairs > 0)
            .Select(r => new { r.StartedAt, r.CrossCheckPairs })
            .ToListAsync(cancellationToken);

        // SQLite cannot sort on a DateTimeOffset.
        return earlierRuns.MaxBy(r => r.StartedAt)?.CrossCheckPairs ?? EstimatedPairs;
    }

    private async Task ReportProgressAsync(
        ActiveRun active,
        HealthCheckOutputParser parser,
        HealthCheckKind check,
        int index,
        int estimatedItems)
    {
        // Never 100 before the end: the estimate may be short.
        int checkPercent = estimatedItems == 0 ? 0 : Math.Min(99, parser.ItemsChecked * 100 / estimatedItems);
        int percent = ((index * 100) + checkPercent) / active.Snapshot.Checks.Count;
        active.Snapshot = active.Snapshot with
        {
            Progress = new HealthCheckProgressDto(check, parser.CurrentItem, parser.ItemsChecked, percent),
        };
        await notifier.HealthCheckChangedAsync(active.Snapshot);
    }

    private async Task AddFindingAsync(ActiveRun active, HealthCheckKind check, HealthCheckProblem? problem)
    {
        if (problem is null)
        {
            return;
        }

        // p4d checks some pairs of tables twice.
        HealthCheckFindingDto finding = new(check, problem.Table, problem.Message);
        if (active.Snapshot.Findings.Count >= MaxFindings || active.Snapshot.Findings.Contains(finding))
        {
            return;
        }

        // Saved at once: it outlives a crash of the daemon. Not canceled: a canceled run keeps what it found.
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None))
        {
            db.HealthCheckFindings.Add(new HealthCheckFinding
            {
                RunId = active.Snapshot.Id,
                Check = check,
                Table = problem.Table,
                Message = problem.Message,
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        active.Snapshot = active.Snapshot with { Findings = [.. active.Snapshot.Findings, finding] };
        await notifier.HealthCheckChangedAsync(active.Snapshot);
    }

    private async Task FinishAsync(
        ActiveRun active,
        Target target,
        HealthCheckStatus? endedAs,
        IReadOnlyList<string> errors)
    {
        // A line of a killed p4d may still be on its way: it must not push the run as running after its end.
        await active.EndAsync();

        Guid instanceId = active.Snapshot.InstanceId;
        bool hasFindings = active.Snapshot.Findings.Count > 0;
        HealthCheckStatus status = endedAs ?? (hasFindings
            ? HealthCheckStatus.ProblemsFound
            : errors.Count > 0 ? HealthCheckStatus.Failed : HealthCheckStatus.Healthy);
        HealthCheckRunDto finished = active.Snapshot with
        {
            Status = status,
            FinishedAt = timeProvider.GetUtcNow(),
            Error = errors.Count > 0 ? string.Join(' ', errors) : null,
            HasFixJournal = KeepFixJournal(active.Snapshot, target),
            Progress = null,
        };

        try
        {
            await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            await db.HealthCheckRuns
                .Where(r => r.Id == finished.Id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(r => r.Status, finished.Status)
                        .SetProperty(r => r.FinishedAt, finished.FinishedAt)
                        .SetProperty(r => r.Error, finished.Error)
                        .SetProperty(r => r.HasFixJournal, finished.HasFixJournal)
                        .SetProperty(r => r.CrossCheckPairs, active.CrossCheckPairs),
                    CancellationToken.None);
            await RemoveReplacedHiddenReportsAsync(instanceId);
        }
        catch (Exception exception)
        {
            // Any failure is caught: nothing awaits this task. The next start of the daemon ends the run.
            logger.LogError(exception, "Could not save the health check of instance {Name}", target.InstanceName);
        }

        activeRuns.TryRemove(instanceId, out _);
        active.Cancellation.Dispose();
        await notifier.HealthCheckChangedAsync(finished);
        logger.LogInformation(
            "The health check of instance {Name} ended: {Status}, {Count} problem(s)",
            target.InstanceName,
            status,
            finished.Findings.Count);
    }

    /// <summary>
    /// Deletes the hidden reports that no longer tell the health of the instance. A run that did not conclude replaces
    /// nothing: they stay.
    /// </summary>
    private async Task RemoveReplacedHiddenReportsAsync(Guid instanceId)
    {
        List<Guid> replaced;
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None))
        {
            List<HealthCheckRun> runs = await db.HealthCheckRuns
                .AsNoTracking()
                .Where(r => r.InstanceId == instanceId)
                .ToListAsync(CancellationToken.None);
            HashSet<Guid> tellingTheHealth = FindRunsTellingTheHealth(runs);
            replaced = runs.Where(r => r.ReportHidden && !tellingTheHealth.Contains(r.Id)).Select(r => r.Id).ToList();
        }

        foreach (Guid runId in replaced)
        {
            await RemoveReportAsync(instanceId, runId);
        }
    }

    private async Task RemoveReportAsync(Guid instanceId, Guid runId)
    {
        await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None))
        {
            await db.HealthCheckRuns.Where(r => r.Id == runId).ExecuteDeleteAsync(CancellationToken.None);
        }

        DeleteFixJournal(paths.GetHealthCheckFixJournalPath(instanceId, runId));
        await notifier.HealthCheckDeletedAsync(instanceId, runId);
    }

    /// <summary>
    /// The latest run of each check that ran to its end. A run cut short says nothing of the database.
    /// </summary>
    private static HashSet<Guid> FindRunsTellingTheHealth(List<HealthCheckRun> runs)
    {
        // SQLite cannot sort on a DateTimeOffset.
        List<HealthCheckRun> conclusive = runs
            .Where(r => r.Status is HealthCheckStatus.Healthy or HealthCheckStatus.ProblemsFound)
            .OrderByDescending(r => r.StartedAt)
            .ToList();
        return Enum.GetValues<HealthCheckKind>()
            .Select(check => conclusive.FirstOrDefault(r => r.Checks.Contains(check)))
            .OfType<HealthCheckRun>()
            .Select(r => r.Id)
            .ToHashSet();
    }

    /// <summary>
    /// Keeps the fix journal when it has something to tell: without an inconsistency, it only holds its header.
    /// </summary>
    private bool KeepFixJournal(HealthCheckRunDto run, Target target)
    {
        if (!File.Exists(target.FixJournalPath))
        {
            return false;
        }
        if (run.Findings.Any(f => f.Check == HealthCheckKind.CrossCheck))
        {
            return true;
        }

        DeleteFixJournal(target.FixJournalPath);
        return false;
    }

    private void DeleteFixJournal(string path)
    {
        try
        {
            // Missing is fine: the run may have kept none.
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete the fix journal {Path}", path);
        }
    }

    /// <summary>
    /// The tables of the root, without the partitioned ones, which <c>p4d -xv</c> leaves out. It also leaves out the
    /// tables it finds nothing to validate in: this only scales the progress.
    /// </summary>
    private static int CountTables(string rootPath)
    {
        try
        {
            return Directory.EnumerateFiles(rootPath, "db.*").Count(file => !file.EndsWith(".pt", StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private sealed record Target(
        string InstanceName,
        string P4dPath,
        IReadOnlyList<string> RootArguments,
        string RootPath,
        string FixJournalPath);

    private sealed class ActiveRun(
        HealthCheckRunDto snapshot,
        Target target,
        bool restartServer,
        CancellationTokenSource cancellation)
    {
        private readonly SemaphoreSlim gate = new(1, 1);

        private volatile HealthCheckRunDto current = snapshot;

        private bool ended;

        public Target Target { get; } = target;

        /// <summary>
        /// Whether p4d ran before the check.
        /// </summary>
        public bool RestartServer { get; } = restartServer;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        /// <summary>
        /// The pairs of tables <c>p4d -xx</c> reached, once it has run to its end.
        /// </summary>
        public int? CrossCheckPairs { get; set; }

        public HealthCheckRunDto Snapshot
        {
            get => current;
            set => current = value;
        }

        /// <summary>
        /// Runs a change of the run, one at a time: the lines of both outputs of p4d come at the same time. Once the run
        /// has ended, the change is dropped.
        /// </summary>
        public async Task WithGateAsync(Func<Task> action)
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                if (!ended)
                {
                    await action();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Waits for the change in progress, and drops the next ones.
        /// </summary>
        public Task EndAsync()
        {
            return WithGateAsync(() =>
            {
                ended = true;
                return Task.CompletedTask;
            });
        }
    }
}
