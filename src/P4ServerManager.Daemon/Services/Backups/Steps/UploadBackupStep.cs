using System.Globalization;

using P4ServerManager.Backup.Manifests;
using P4ServerManager.Backup.Repository;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Steps;

/// <summary>
/// Makes the steps that upload a backup, one per destination.
/// </summary>
public sealed class UploadBackupStepFactory(BackupRunNotifier runNotifier, ILoggerFactory loggerFactory)
{
    /// <param name="index">Position of the destination in <see cref="BackupContext.Destinations"/>.</param>
    /// <param name="destinationName">Name of the destination, for the title of the step.</param>
    public UploadBackupStep Create(int index, string destinationName)
    {
        return new UploadBackupStep(index, destinationName, runNotifier, loggerFactory.CreateLogger<UploadBackupStep>());
    }
}

/// <summary>
/// Uploads to one destination the objects it does not have yet, then writes the manifest of the point, which makes it
/// visible. A failure ends the backup at this destination only: no manifest is written, so no partial point is left.
/// </summary>
public sealed class UploadBackupStep(
    int index,
    string destinationName,
    BackupRunNotifier runNotifier,
    ILogger<UploadBackupStep> logger) : IOperationStep<BackupContext>
{
    private const int MaxParallelUploads = 4;

    public string Key => "upload-backup";

    public string Title => $"Upload to {destinationName}";

    public bool ShouldRun(BackupContext context)
    {
        return !context.Destinations[index].Failed;
    }

    public async Task<string?> RunAsync(
        BackupContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        BackupDestinationState state = context.Destinations[index];
        BackupRepository repository = state.Opened?.Repository
            ?? throw new InvalidOperationException($"Destination {destinationName} is not open.");
        state.Record.Status = BackupDestinationStatus.Uploading;
        await runNotifier.SaveAsync(context.Db, context.Run);

        int uploadedObjects = 0;
        long uploadedBytes = 0;
        int missingArchives = 0;
        try
        {
            List<BackupFile> files = [.. context.MetadataFiles, .. context.ServerFiles];
            BackupManifestEntry?[] entries = new BackupManifestEntry?[files.Count];
            long totalBytes = Math.Max(1, files.Sum(f => f.Size));
            long processedBytes = 0;
            int lastPercent = -1;
            object progressLock = new();

            await Parallel.ForEachAsync(
                Enumerable.Range(0, files.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelUploads, CancellationToken = cancellationToken },
                async (i, token) =>
                {
                    BackupFile file = files[i];
                    (BackupManifestEntry Entry, StoredObject? Stored)? result =
                        await StoreAsync(repository, state, file, token);
                    entries[i] = result is { } stored ? Count(stored.Entry, stored.Stored) : null;

                    long processed = Interlocked.Add(ref processedBytes, file.Size);
                    int percent = (int)(processed * 100 / totalBytes);
                    lock (progressLock)
                    {
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress.Report(new OperationStepProgress(
                                $"{Volatile.Read(ref uploadedObjects)} objects uploaded",
                                Math.Min(percent, 100)));
                        }
                    }
                });

            missingArchives = entries.Count(e => e is null);
            IEnumerable<BackupManifestEntry> pointEntries = entries.OfType<BackupManifestEntry>();
            if (context.Kind == BackupKind.Incremental)
            {
                // The point lists the checkpoint and the journals it builds on, already at the destination.
                pointEntries = state.Base!.Entries
                    .Where(e => BackupFileRoles.IsMetadata(e.Role))
                    .Concat(pointEntries);
            }

            BackupSnapshotInfo snapshot = await repository.WriteManifestAsync(
                CreateHeader(context, state),
                pointEntries,
                cancellationToken);
            state.Record.Status = BackupDestinationStatus.Succeeded;
            state.Record.SnapshotKey = snapshot.Key;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Backup of instance {Name} to {Destination} failed",
                context.Instance.Name,
                destinationName);
            state.Fail(exception.Message);
        }
        finally
        {
            state.Record.UploadedObjects = uploadedObjects;
            state.Record.UploadedBytes = uploadedBytes;
        }

        await runNotifier.SaveAsync(context.Db, context.Run);
        if (state.Failed)
        {
            return $"Failed: {state.Record.Error}";
        }

        string summary = $"{uploadedObjects} objects uploaded ({FormatBytes(uploadedBytes)})";
        return missingArchives > 0
            ? $"{summary}; {missingArchives} archives deleted meanwhile were left out"
            : summary;

        BackupManifestEntry Count(BackupManifestEntry entry, StoredObject? stored)
        {
            if (stored is { Uploaded: true })
            {
                Interlocked.Increment(ref uploadedObjects);
                Interlocked.Add(ref uploadedBytes, stored.StoredSize);
            }
            return entry;
        }
    }

    /// <summary>
    /// Uploads the content of a file unless the destination has it.
    /// </summary>
    /// <returns>
    /// The entry of the file in the point, and what was uploaded if anything; null for an archive deleted since it was
    /// listed, such as by an obliterate.
    /// </returns>
    private static async Task<(BackupManifestEntry Entry, StoredObject? Stored)?> StoreAsync(
        BackupRepository repository,
        BackupDestinationState state,
        BackupFile file,
        CancellationToken cancellationToken)
    {
        string name = repository.GetObjectName(file.Sha256);
        if (state.KnownObjects.Contains(name) || await repository.ObjectExistsAsync(name, cancellationToken))
        {
            return (ToEntry(file, file.Size, file.Sha256, name), null);
        }

        try
        {
            StoredObject stored = await repository.PutFileAsync(file.FullPath, file.Compress, cancellationToken);
            // The file may have changed since it was listed: the point tells what was stored.
            return (ToEntry(file, stored.Size, stored.Sha256, stored.Name), stored);
        }
        catch (Exception exception) when (file.Role == BackupFileRoles.Archive
            && exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static BackupManifestEntry ToEntry(BackupFile file, long size, string sha256, string name)
    {
        return new BackupManifestEntry(file.Role, file.ManifestPath, size, file.ModifiedAt, sha256, name);
    }

    private static BackupManifestHeader CreateHeader(BackupContext context, BackupDestinationState state)
    {
        bool incremental = context.Kind == BackupKind.Incremental;
        string? checkpoint = incremental
            ? state.Base!.Header.Labels.GetValueOrDefault(BackupLabels.CheckpointNumber)
            : context.Run.CheckpointNumber?.ToString(CultureInfo.InvariantCulture);

        Dictionary<string, string> labels = new()
        {
            [BackupLabels.P4dVersion] = $"{context.Instance.Build?.Version}/{context.Instance.Build?.BuildNumber}",
            [BackupLabels.Unicode] = context.Instance.Unicode ? "true" : "false",
            [BackupLabels.CaseSensitive] = context.Instance.CaseSensitive ? "true" : "false",
        };
        if (checkpoint is not null)
        {
            labels[BackupLabels.CheckpointNumber] = checkpoint;
        }
        if (incremental && context.Run.JournalNumber is { } journal)
        {
            labels[BackupLabels.JournalNumber] = journal.ToString(CultureInfo.InvariantCulture);
        }
        if (context.ServerId is { } serverId)
        {
            labels[BackupLabels.ServerId] = serverId;
        }
        foreach (ObjectStoreDepot depot in context.Layout?.ObjectStoreDepots ?? [])
        {
            labels[BackupLabels.ObjectStoreDepotPrefix + depot.Name] = depot.ObjectAddress;
        }

        return new BackupManifestHeader
        {
            Id = context.Run.Id,
            SourceId = context.Instance.Id,
            SourceName = context.Instance.Name,
            Kind = incremental ? "incremental" : "full",
            BaseId = incremental ? state.Base!.Header.Id : null,
            CreatedAt = context.Run.StartedAt,
            Labels = labels,
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
