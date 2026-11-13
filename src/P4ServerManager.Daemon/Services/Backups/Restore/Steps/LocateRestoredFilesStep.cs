using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Reads the layout of the restored database, offline, and places each file of the point in it. No file may go to the
/// folders of another instance.
/// </summary>
public sealed class LocateRestoredFilesStep(ServerFileLocator locator, DaemonPaths paths) : IOperationStep<RestoreContext>
{
    public string Key => "locate-restored-files";

    public string Title => "Place the files of the point";

    public async Task<string?> RunAsync(
        RestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        ServerDescription target;
        try
        {
            // The server.id of the point is restored with the files, after this read; a new instance has its own.
            target = context.ReplacesData
                ? await locator.ReadRestoredDescriptionAsync(context.Instance, context.PointServerId, cancellationToken)
                : await locator.ReadDescriptionAsync(context.Instance, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new InstanceOperationException(
                $"The layout of the restored server cannot be read for sure. {exception.Message}");
        }
        ServerDescription original = context.OriginalDescription ?? target;

        string rootPath = Path.GetFullPath(context.Instance.RootPath);
        var others = await context.Db.Instances
            .AsNoTracking()
            .Where(i => i.Id != context.Instance.Id)
            .Select(i => new { i.Id, i.RootPath })
            .ToListAsync(cancellationToken);
        List<string> forbidden = [];
        foreach (var other in others)
        {
            forbidden.Add(other.RootPath);
            string instanceDirectory = paths.GetInstanceDirectory(other.Id);
            if (ServerFileLocator.Contains(instanceDirectory, other.RootPath))
            {
                forbidden.Add(instanceDirectory);
            }
        }

        RestorePlan plan;
        try
        {
            ServerFileLayout originalLayout = ServerFileLocator.Resolve(rootPath, original.Configurables, original.Depots);
            ServerFileLayout targetLayout = ServerFileLocator.Resolve(rootPath, target.Configurables, target.Depots);
            plan = RestorePlanner.Plan(
                context.RequireManifest().Entries,
                originalLayout,
                targetLayout,
                original.Depots,
                new RestorePlanOptions(
                    RelocateDepots: !context.ReplacesData,
                    RestoreServerId: context.ReplacesData,
                    RestoreLicense: context.RestoreLicense,
                    ForbiddenDirectories: forbidden));
        }
        catch (InvalidDataException exception)
        {
            throw new InstanceOperationException(exception.Message);
        }

        context.Plan = plan;
        foreach (string warning in plan.Warnings)
        {
            context.AddWarning(warning);
        }
        return $"{plan.Files.Count} files in {plan.Directories.Count} folders";
    }
}
