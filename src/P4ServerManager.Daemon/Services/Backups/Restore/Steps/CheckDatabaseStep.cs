using System.Globalization;

using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Validates the restored database with <c>p4d -xv</c>, before the server starts. A damaged database fails the restore.
/// </summary>
public sealed class CheckDatabaseStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner) : IOperationStep<RestoreContext>
{
    private const int ReportedProblems = 5;

    public string Key => "check-database";

    public string Title => "Check the restored database";

    public async Task<string?> RunAsync(
        RestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        HealthCheckOutputParser parser = new();
        List<string> problems = [];
        object gate = new();
        void Add(HealthCheckProblem? problem)
        {
            if (problem is not null)
            {
                lock (gate)
                {
                    problems.Add(problem.Table is null ? problem.Message : $"{problem.Table}: {problem.Message}");
                }
            }
        }

        ProcessCommand command = new(
            buildCache.GetP4dPath(context.Instance.Build
                ?? throw new InvalidOperationException("The instance has no p4d build yet.")),
            [.. P4ServerLauncher.GetRootArguments(context.Instance), "-xv"],
            Timeout: Timeout.InfiniteTimeSpan,
            OnOutputLine: line =>
            {
                lock (gate)
                {
                    Add(parser.ReadOutputLine(line));
                    progress.Report(new OperationStepProgress(parser.CurrentItem, null));
                }
                return Task.CompletedTask;
            },
            OnErrorLine: line =>
            {
                lock (gate)
                {
                    Add(parser.ReadErrorLine(line));
                }
                return Task.CompletedTask;
            });
        ProcessResult result = await processRunner.RunAsync(command, cancellationToken);
        Add(parser.Complete());
        problems.AddRange(parser.Errors);

        if (problems.Count > 0)
        {
            throw new InstanceOperationException(
                "The restored database is damaged: " + string.Join(" ", problems.Take(ReportedProblems)));
        }
        if (!result.Succeeded)
        {
            throw new InstanceOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"p4d -xv exited with code {result.ExitCode} on the restored database. {result.ErrorText}"));
        }
        return string.Create(CultureInfo.InvariantCulture, $"{parser.ItemsChecked} tables, no problem found");
    }
}
