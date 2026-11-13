using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Backups.Destinations;

namespace P4ServerManager.Daemon.Contracts;

/// <summary>
/// Maps the backups of the daemon to the types of the API.
/// </summary>
public static class BackupContractMappings
{
    /// <param name="destination">The destination, with its <see cref="BackupDestination.Instances"/> loaded.</param>
    /// <param name="location">Where its repository is, from <see cref="BackupDestinationService.GetLocation"/>.</param>
    public static BackupDestinationDto ToDto(this BackupDestination destination, string location)
    {
        BackupDestinationConfiguration settings = BackupDestinationConfiguration.Deserialize(
            destination.Kind,
            destination.Settings,
            new BackupDestinationCredentials());
        return new BackupDestinationDto(
            destination.Id,
            destination.Name,
            destination.Kind,
            location,
            settings.Local,
            settings.S3,
            settings.Gcs,
            settings.Azure,
            settings.Ftp,
            destination.ProtectedCredentials is not null,
            destination.RepositoryId,
            destination.Instances.Count,
            destination.CreatedAt,
            destination.UpdatedAt);
    }

    /// <param name="run">The run, with its <see cref="BackupRun.Destinations"/> loaded.</param>
    public static BackupRunDto ToDto(this BackupRun run)
    {
        return new BackupRunDto(
            run.Id,
            run.InstanceId,
            run.InstanceName,
            run.Kind,
            run.Trigger,
            run.Status,
            run.StartedAt,
            run.FinishedAt,
            run.CheckpointNumber,
            run.JournalNumber,
            run.FileCount,
            run.TotalBytes,
            run.Error,
            run.Destinations.OrderBy(d => d.Id).Select(ToDto).ToList());
    }

    public static BackupRunDestinationDto ToDto(this BackupRunDestination destination)
    {
        BackupVerificationDto? verification = destination.VerifiedAt is { } verifiedAt
            ? new BackupVerificationDto(
                verifiedAt,
                destination.VerificationDeep,
                destination.VerificationSucceeded,
                destination.VerificationCheckedObjects,
                destination.VerificationProblemCount,
                destination.VerificationProblems,
                destination.VerificationError)
            : null;
        return new BackupRunDestinationDto(
            destination.DestinationId,
            destination.DestinationName,
            destination.Status,
            destination.UploadedObjects,
            destination.UploadedBytes,
            destination.SnapshotKey,
            destination.Error,
            verification);
    }
}
