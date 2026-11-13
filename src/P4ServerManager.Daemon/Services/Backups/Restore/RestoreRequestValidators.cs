using FluentValidation;

using P4ServerManager.Backup.Repository;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Backups.Restore;

/// <summary>
/// Checks a restore over an instance: a destination, and the key of a backup point.
/// </summary>
public sealed class RestoreBackupRequestValidator : AbstractValidator<RestoreBackupRequest>
{
    public RestoreBackupRequestValidator()
    {
        RuleFor(r => r.DestinationId).NotEmpty();
        RuleFor(r => r.SnapshotKey).MustBeSnapshotKey();
    }
}

/// <summary>
/// Checks a restore into a new instance against the other instances, the p4d builds and the machine, as a creation.
/// The password of the super user is the one of the restored server, whatever its strength.
/// </summary>
public sealed class RestoreInstanceRequestValidator : AbstractValidator<RestoreInstanceRequest>
{
    public RestoreInstanceRequestValidator(DaemonDbContext db, IP4ServerReleaseCatalog releaseCatalog)
    {
        // One error per field: the checks against the database or the machine only run on a well-formed value.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(r => r.Name).MustBeNewInstanceName(db);
        RuleFor(r => r.Description).MustBeInstanceDescription();
        RuleFor(r => r.Port).MustBeFreeInstancePort(db);
        RuleFor(r => r.Release!)
            .MustBeAvailableRelease(releaseCatalog, db)
            .When(r => r.Release is not null);
        RuleFor(r => r.Build!)
            .Must((request, _) => request.Release is not null)
            .WithMessage("A build needs its release.")
            .MustBeArchivedBuild(db, r => r.Release!)
            .When(r => r.Build is not null);

        RuleFor(r => r.ServerDirectory!)
            .Must(Path.IsPathFullyQualified)
            .WithMessage("The server directory is an absolute path.")
            .Must(path => !File.Exists(path))
            .WithMessage("The server directory is a file.")
            .Must(path => !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
            .WithMessage("The server directory is not empty.")
            .When(r => !string.IsNullOrWhiteSpace(r.ServerDirectory));

        RuleFor(r => r.SuperUser.Name).MustBeUserName();
        RuleFor(r => r.SuperUser.Password).NotEmpty();

        RuleFor(r => r.Source.DestinationId).NotEmpty();
        RuleFor(r => r.Source.SnapshotKey).MustBeSnapshotKey();
    }
}

public static class RestoreRules
{
    public static IRuleBuilderOptions<T, string> MustBeSnapshotKey<T>(this IRuleBuilder<T, string> rule)
    {
        return rule
            .Must(key => key is not null && BackupRepository.TryParseManifestKey(key, out _))
            .WithMessage("This is not the key of a backup point.");
    }
}
