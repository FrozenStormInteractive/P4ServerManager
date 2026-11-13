using Microsoft.EntityFrameworkCore;

using P4ServerManager.Backup.Crypto;
using P4ServerManager.Backup.Targets;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Destinations;
using P4ServerManager.Daemon.Services.Backups.Restore.Steps;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Backups.Restore;

/// <summary>
/// Makes new instances from backup points: reads the point, chooses the p4d build, then adds the instance with its
/// restore, which runs in the background as its provisioning. Scoped: it saves through the context of the request.
/// </summary>
public sealed class RestoredInstanceService(
    DaemonDbContext db,
    BackupDestinationService destinationService,
    BackupPointService pointService,
    InstanceCreationService creationService,
    InstanceNotifier notifier,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Makes a new instance from a backup point. Its case handling and Unicode mode come from the point.
    /// </summary>
    /// <param name="request">The instance and the point, checked by <see cref="RestoreInstanceRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the creation before the instance is saved.</param>
    /// <returns>The instance, saved, with its restore queued.</returns>
    /// <exception cref="KeyNotFoundException">The destination does not exist.</exception>
    /// <exception cref="RestoreSourceException">
    /// The point cannot be read, or the p4d build asked for cannot read it.
    /// </exception>
    /// <exception cref="InstanceConflictException">Another instance took the name or the port in the meantime.</exception>
    public async Task<P4ServerInstance> CreateAsync(RestoreInstanceRequest request, CancellationToken cancellationToken)
    {
        BackupDestination destination = await db.BackupDestinations
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == request.Source.DestinationId, cancellationToken)
            ?? throw new KeyNotFoundException("This backup destination does not exist.");

        BackupPoint point = await ReadPointAsync(destination, request.Source.SnapshotKey, cancellationToken);
        (string release, Guid? buildId) = await ChooseBuildAsync(request, point, cancellationToken);

        P4ServerInstance instance = creationService.NewInstance(
            new NewInstanceSettings(
                request.Name,
                request.Description,
                request.Port,
                request.Ssl,
                request.ServerDirectory,
                request.AutoStart,
                request.SuperUser),
            release,
            buildId,
            point.CaseSensitive,
            point.Unicode);
        RestoreRun run = new()
        {
            Id = Guid.NewGuid(),
            InstanceId = instance.Id,
            InstanceName = instance.Name,
            Target = RestoreTarget.NewInstance,
            DestinationId = destination.Id,
            DestinationName = destination.Name,
            SnapshotKey = request.Source.SnapshotKey,
            PointId = point.Header.Id,
            PointCreatedAt = point.Header.CreatedAt,
            SourceName = point.Header.SourceName,
            Status = RestoreRunStatus.Running,
            StartedAt = timeProvider.GetUtcNow(),
        };

        await creationService.AddAsync(
            instance,
            request.SuperUser,
            ProvisioningKind.Restore,
            new RestoreProvisioning(run.Id, request.IncludeLicense),
            [run],
            cancellationToken);
        await notifier.RestoreChangedAsync(run.ToDto());
        return instance;
    }

    private async Task<BackupPoint> ReadPointAsync(
        BackupDestination destination,
        string snapshotKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await using OpenedBackupDestination opened = await destinationService.OpenAsync(destination, cancellationToken);
            return await pointService.GetAsync(opened, snapshotKey, cancellationToken);
        }
        catch (BackupDestinationException exception)
        {
            throw new RestoreSourceException(exception.Message, exception);
        }
        catch (BackupObjectNotFoundException exception)
        {
            throw new RestoreSourceException($"Destination {destination.Name} has no such backup point.", exception);
        }
        catch (BackupIntegrityException exception)
        {
            throw new RestoreSourceException($"The backup point cannot be read: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// The release and the build of the new instance: the ones asked for, which cannot be older than the build of the
    /// point, or the build of the point when the archive has it, or else the latest build of its release.
    /// </summary>
    /// <exception cref="RestoreSourceException">The build asked for cannot read the point, or none can be chosen.</exception>
    private async Task<(string Release, Guid? BuildId)> ChooseBuildAsync(
        RestoreInstanceRequest request,
        BackupPoint point,
        CancellationToken cancellationToken)
    {
        string[]? pointBuild = point.P4dVersion?.Split('/');
        P4ServerRelease? pointRelease = pointBuild is [{ } version, _]
            && P4ServerRelease.TryParseVersion(version, out P4ServerRelease parsed)
                ? parsed
                : null;

        if (request.Release is null)
        {
            if (pointRelease is not { } release)
            {
                throw new RestoreSourceException("The point does not tell its p4d release: choose one.");
            }
            Guid? archived = await db.Builds
                .Where(b => b.Release == release.Name && b.BuildNumber == pointBuild![1])
                .Select(b => (Guid?)b.Id)
                .FirstOrDefaultAsync(cancellationToken);
            return (release.Name, archived);
        }

        if (!P4ServerRelease.TryParse(request.Release, out P4ServerRelease asked))
        {
            throw new RestoreSourceException($"{request.Release} is not a p4d release.");
        }
        if (pointRelease is { } made && asked < made)
        {
            throw new RestoreSourceException($"The point was made with p4d {point.P4dVersion}: {asked} cannot read it.");
        }
        if (request.Build is null)
        {
            return (asked.Name, null);
        }

        P4ServerBuild build = await db.Builds
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Release == asked.Name && b.BuildNumber == request.Build, cancellationToken)
            ?? throw new RestoreSourceException($"Build {request.Build} of {asked} is not in the archive.");
        return OpenRestoreSourceStep.IsNewerThan(point, build)
            ? throw new RestoreSourceException(
                $"The point was made with p4d {point.P4dVersion}, newer than {build.Version}/{build.BuildNumber}.")
            : (asked.Name, build.Id);
    }
}

/// <summary>
/// A backup point cannot become a new instance: it cannot be read, or the p4d build asked for cannot read it.
/// </summary>
public sealed class RestoreSourceException(string message, Exception? innerException = null)
    : Exception(message, innerException);
