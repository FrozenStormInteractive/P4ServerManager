using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Ends a restore that succeeded: deletes the previous data kept aside, and has the
/// stored super user checked against the restored server, whose passwords are the ones of the point.
/// </summary>
public sealed class FinishRestoreStep(SuperUserMonitor superUserMonitor) : IOperationStep<RestoreContext>
{
    public string Key => "finish-restore";

    public string Title => "Finish the restore";

    public async Task<string?> RunAsync(
        RestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        string? summary = null;
        if (context.SetAside is { } setAside)
        {
            // From here, the restore cannot be undone.
            context.SetAside = null;
            progress.Report(new OperationStepProgress("Delete the previous data", null));
            IReadOnlyList<string> left = setAside.Commit();
            foreach (string folder in left)
            {
                context.AddWarning($"{folder} holds the previous data and could not be deleted: delete it by hand.");
            }
            summary = left.Count == 0 ? "Previous data deleted" : $"{left.Count} folders of the previous data left";
        }

        context.Instance.LastError = null;
        superUserMonitor.RequestCheck(context.Instance.Id);
        return summary;
    }
}
