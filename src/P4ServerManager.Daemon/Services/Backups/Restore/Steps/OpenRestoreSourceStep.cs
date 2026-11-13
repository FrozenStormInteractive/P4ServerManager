using P4ServerManager.Backup.Crypto;
using P4ServerManager.Backup.Manifests;
using P4ServerManager.Backup.Repository;
using P4ServerManager.Backup.Targets;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Collection;
using P4ServerManager.Daemon.Services.Backups.Destinations;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Opens the destination and reads the point, then checks that the instance can take it: the same case handling, and a
/// p4d build no older than the one that made the point. Nothing is touched yet.
/// </summary>
public sealed class OpenRestoreSourceStep(
    BackupDestinationService destinationService,
    RestoreRunNotifier runNotifier) : IOperationStep<RestoreContext>
{
    public string Key => "open-restore-source";

    public string Title => "Open the backup point";

    public async Task<string?> RunAsync(
        RestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new OperationStepProgress(context.Destination.Name, null));
        try
        {
            context.Opened = await destinationService.OpenAsync(context.Destination, cancellationToken);
        }
        catch (BackupDestinationException exception)
        {
            throw new InstanceOperationException(exception.Message);
        }

        BackupManifest manifest;
        try
        {
            manifest = await context.Opened.Repository.ReadManifestAsync(context.Run.SnapshotKey, cancellationToken);
        }
        catch (BackupObjectNotFoundException)
        {
            throw new InstanceOperationException(
                $"Destination {context.Destination.Name} has no backup point {context.Run.SnapshotKey}.");
        }
        catch (BackupIntegrityException exception)
        {
            throw new InstanceOperationException($"The backup point cannot be read: {exception.Message}");
        }

        BackupRepository.TryParseManifestKey(context.Run.SnapshotKey, out BackupSnapshotInfo? snapshot);
        BackupPoint point = new(
            snapshot ?? new BackupSnapshotInfo(context.Run.SnapshotKey, manifest.Header.SourceId, manifest.Header.CreatedAt, manifest.Header.Id),
            manifest.Header);
        if (!manifest.Entries.Any(e => e.Role == BackupFileRoles.Checkpoint))
        {
            throw new InstanceOperationException("The backup point has no checkpoint: it cannot be restored.");
        }

        if (context.ReplacesData)
        {
            CheckFits(point, context.Instance);
        }
        else
        {
            // A new instance takes the case handling and the Unicode mode of the point.
            context.Instance.CaseSensitive = point.CaseSensitive;
            context.Instance.Unicode = point.Unicode;
            if (context is NewInstanceRestoreContext created)
            {
                created.OldServerId = point.ServerId;
                created.OldPort = point.Port ?? context.Instance.Port;
            }
        }

        context.Manifest = manifest;
        context.PointServerId = point.ServerId;
        context.Run.PointId = manifest.Header.Id;
        context.Run.PointCreatedAt = manifest.Header.CreatedAt;
        context.Run.SourceName = manifest.Header.SourceName;
        context.Run.FileCount = manifest.Entries.Count;
        context.Run.TotalBytes = manifest.Entries.Sum(e => e.Size);
        await runNotifier.SaveAsync(context.Db, context.Run);
        return $"{context.Run.FileCount} files, {ByteSize.Format(context.Run.TotalBytes)}, from {manifest.Header.SourceName}";
    }

    /// <summary>
    /// Checks that an instance can take a point: the same case handling, and a build no older than the one of the point.
    /// </summary>
    /// <exception cref="InstanceOperationException">It cannot.</exception>
    public static void CheckFits(BackupPoint point, P4ServerInstance instance)
    {
        if (point.CaseSensitive != instance.CaseSensitive)
        {
            throw new InstanceOperationException(point.CaseSensitive
                ? $"The point comes from a case-sensitive server, and instance {instance.Name} is not."
                : $"The point comes from a case-insensitive server, and instance {instance.Name} is not.");
        }

        if (instance.Build is { } build && IsNewerThan(point, build))
        {
            throw new InstanceOperationException(
                $"The point was made with p4d {point.P4dVersion}, newer than {build.Version}/{build.BuildNumber} of "
                + $"instance {instance.Name}: upgrade the instance first.");
        }
    }

    /// <summary>
    /// Whether the point was made with a newer p4d than <paramref name="build"/>, which cannot read its checkpoint.
    /// </summary>
    public static bool IsNewerThan(BackupPoint point, P4ServerBuild build)
    {
        if (point.P4dVersion?.Split('/') is not [{ } version, { } number])
        {
            return false;
        }

        P4ServerBuild pointBuild = new()
        {
            Release = build.Release,
            Version = version,
            BuildNumber = number,
            Platform = build.Platform,
            Sha256 = string.Empty,
        };
        return P4ServerBuildNumber.Compare(pointBuild, build) > 0;
    }
}
