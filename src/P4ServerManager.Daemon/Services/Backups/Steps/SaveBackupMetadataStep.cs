using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Backups.Steps;

/// <summary>
/// Freezes the database before any archive is read: a full backup takes a checkpoint and rotates the journal
/// (<c>p4d -jc</c>), an incremental one rotates the journal only (<c>p4d -jj</c>). Both work while p4d runs, and leave
/// their compressed files where <c>journalPrefix</c> says, as an administrator would find them.
/// </summary>
public sealed class SaveBackupMetadataStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    ServerFileLocator locator,
    BackupRunNotifier runNotifier,
    DaemonPaths paths) : IOperationStep<BackupContext>
{
    /// <summary>
    /// A checkpoint reads every table: it takes as long as the database is big.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(4);

    public string Key => "save-backup-metadata";

    public string Title => "Save the database";

    public async Task<string?> RunAsync(
        BackupContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        var instance = context.Instance;
        var layout = await locator.ReadLayoutAsync(instance, cancellationToken);
        context.Layout = layout;
        context.Index = await BackupFileIndex.LoadAsync(paths.GetBackupIndexPath(instance.Id), cancellationToken);

        Directory.CreateDirectory(layout.JournalDirectory);
        HashSet<string> before = ListJournalFiles(layout).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        bool full = context.Kind == BackupKind.Full;
        progress.Report(new OperationStepProgress(full ? "Taking a checkpoint" : "Rotating the journal", null));
        await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(instance.Build!),
                [.. P4ServerLauncher.GetRootArguments(instance), "-z", full ? "-jc" : "-jj"],
                Timeout: Timeout),
            cancellationToken);

        List<(string Path, JournalFile File)> created = ListJournalFiles(layout)
            .Where(f => !before.Contains(f.Path))
            .ToList();
        List<(string Path, JournalFile File)> kept;
        if (full)
        {
            (string Path, JournalFile File) checkpoint = created
                .Where(f => f.File.Kind == JournalFileKind.Checkpoint)
                .OrderByDescending(f => f.File.Number)
                .FirstOrDefault();
            if (checkpoint.Path is null)
            {
                throw new InstanceOperationException($"p4d wrote no checkpoint to {layout.JournalDirectory}.");
            }
            context.Run.CheckpointNumber = checkpoint.File.Number;
            kept = created
                .Where(f => f.File.Number == checkpoint.File.Number
                    && f.File.Kind is JournalFileKind.Checkpoint or JournalFileKind.Checksum)
                .ToList();
        }
        else
        {
            (string Path, JournalFile File) journal = created
                .Where(f => f.File.Kind == JournalFileKind.Journal)
                .OrderByDescending(f => f.File.Number)
                .FirstOrDefault();
            if (journal.Path is null)
            {
                throw new InstanceOperationException($"p4d wrote no rotated journal to {layout.JournalDirectory}.");
            }
            context.Run.JournalNumber = journal.File.Number;
            kept = [journal];
            await CheckChainsAsync(context, journal.File.Number);
        }

        foreach ((string path, JournalFile file) in kept)
        {
            string role = file.Kind switch
            {
                JournalFileKind.Checkpoint => BackupFileRoles.Checkpoint,
                JournalFileKind.Checksum => BackupFileRoles.Checksum,
                _ => BackupFileRoles.Journal,
            };
            BackupFile? read = await context.Index.ReadAsync(
                role,
                $"journals/{Path.GetFileName(path)}",
                path,
                cancellationToken);
            context.MetadataFiles.Add(read
                ?? throw new InstanceOperationException($"{path} disappeared right after p4d wrote it."));
        }

        return full
            ? $"Checkpoint {context.Run.CheckpointNumber}"
            : $"Journal {context.Run.JournalNumber}";
    }

    /// <summary>
    /// Fails the destinations whose latest point does not end right before <paramref name="journalNumber"/>: they miss
    /// journals, and only a full backup can go there. p4d tells the number of the journal only by rotating it, so the
    /// check comes after the rotation. A journal rotated for nothing stays with the others, harmless.
    /// </summary>
    private async Task CheckChainsAsync(BackupContext context, int journalNumber)
    {
        foreach (BackupDestinationState state in context.Destinations.Where(d => !d.Failed))
        {
            if (OpenBackupDestinationsStep.CheckChain(state.Base, journalNumber) is { } problem)
            {
                state.Fail(problem);
            }
        }

        await runNotifier.SaveAsync(context.Db, context.Run);
        if (context.Destinations.All(d => d.Failed))
        {
            throw new InstanceOperationException(
                "No destination can take the backup. "
                + string.Join(" ", context.Destinations.Select(d => $"{d.Destination.Name}: {d.Record.Error}")));
        }
    }

    private static IEnumerable<(string Path, JournalFile File)> ListJournalFiles(ServerFileLayout layout)
    {
        if (!Directory.Exists(layout.JournalDirectory))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(layout.JournalDirectory))
        {
            if (ServerFileLocator.ParseJournalFileName(Path.GetFileName(path), layout.JournalPrefix) is { } file)
            {
                yield return (path, file);
            }
        }
    }
}
