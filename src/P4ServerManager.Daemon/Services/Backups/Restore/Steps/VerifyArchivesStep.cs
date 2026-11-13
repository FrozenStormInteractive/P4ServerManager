using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Runs <c>p4 verify</c> on the restored server as its super user, for the submitted files then the shelved ones. What
/// it finds is reported with the restore, which still succeeds: the instance is kept. Without a usable super user, the
/// verification is skipped with a warning. A cancellation stops only the verification.
/// </summary>
public sealed class VerifyArchivesStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    RestoreRunNotifier runNotifier,
    ILogger<VerifyArchivesStep> logger) : IOperationStep<RestoreContext>
{
    public const int MaxProblems = 100;

    public string Key => "verify-archives";

    public string Title => "Verify the archives";

    /// <summary>
    /// Whether a line of <c>p4 verify</c> tells a damaged or missing revision.
    /// </summary>
    public static bool IsProblem(string line)
    {
        return line.Contains(" BAD!", StringComparison.Ordinal) || line.Contains(" MISSING!", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a failure of <c>p4 verify</c> only says that there is nothing to verify.
    /// </summary>
    public static bool IsNothingToVerify(string error)
    {
        return error.Contains("no such file(s)", StringComparison.OrdinalIgnoreCase)
            || error.Contains("file(s) not shelved", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no shelved files", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> RunAsync(
        RestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        if (context.SuperUser is not { } superUser)
        {
            context.AddWarning("The archives were not verified: no usable super user is stored for the instance.");
            await runNotifier.SaveAsync(context.Db, context.Run);
            return "Skipped: no usable super user";
        }

        RestoreRun run = context.Run;
        object gate = new();
        Task OnLine(string line)
        {
            if (IsProblem(line))
            {
                lock (gate)
                {
                    run.VerifyProblemCount++;
                    if (run.VerifyProblems.Count < MaxProblems)
                    {
                        run.VerifyProblems.Add(line.Trim());
                    }
                }
            }
            return Task.CompletedTask;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.VerifyCancellation.Token);
        context.Verifying = true;
        try
        {
            P4AdminSession session = await P4AdminSession.OpenAsync(
                buildCache,
                processRunner,
                paths,
                logger,
                context.Instance,
                superUser,
                linked.Token);
            await session.RunLoggedInAsync(
                async () =>
                {
                    string[][] commands = [["verify", "-q", "//..."], ["verify", "-qS", "//..."]];
                    foreach (string[] command in commands)
                    {
                        progress.Report(new OperationStepProgress($"p4 {string.Join(' ', command)}", null));
                        ProcessResult result = await session.RunLongAsync(command, OnLine, linked.Token);
                        if (!result.Succeeded && run.VerifyProblemCount == 0 && !IsNothingToVerify(result.ErrorText))
                        {
                            context.AddWarning($"p4 {string.Join(' ', command)} failed: {result.ErrorText}");
                        }
                    }
                    return true;
                },
                linked.Token);
        }
        catch (InstanceOperationException exception)
        {
            context.AddWarning($"The archives were not verified: {exception.Message}");
        }
        catch (OperationCanceledException)
            when (context.VerifyCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            context.AddWarning("The verification of the archives was canceled.");
        }
        finally
        {
            context.Verifying = false;
        }

        await runNotifier.SaveAsync(context.Db, run);
        return run.VerifyProblemCount == 0 ? "No problem found" : $"{run.VerifyProblemCount} problems found";
    }
}
