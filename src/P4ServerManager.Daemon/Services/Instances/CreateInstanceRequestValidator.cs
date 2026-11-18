using FluentValidation;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Checks a <see cref="CreateInstanceRequest"/> against the other instances, the p4d builds and the machine.
/// </summary>
public sealed class CreateInstanceRequestValidator : AbstractValidator<CreateInstanceRequest>
{
    public CreateInstanceRequestValidator(
        DaemonDbContext db,
        IP4ServerReleaseCatalog releaseCatalog,
        IValidator<SuperUserRequest> superUserValidator,
        DaemonPaths paths)
    {
        // One error per field: the checks against the database or the machine only run on a well-formed value.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(r => r.Name)
            .MustBeNewInstanceName(db)
            .MustHaveFreeDefaultServerDirectory(paths)
            .When(r => string.IsNullOrWhiteSpace(r.ServerDirectory), ApplyConditionTo.CurrentValidator);
        RuleFor(r => r.Description).MustBeInstanceDescription();
        RuleFor(r => r.Port).MustBeFreeInstancePort(db);
        RuleFor(r => r.Release).MustBeAvailableRelease(releaseCatalog, db);
        RuleFor(r => r.Build!)
            .MustBeArchivedBuild(db, r => r.Release)
            .When(r => r.Build is not null);

        RuleFor(r => r.ServerDirectory!)
            .Must(Path.IsPathFullyQualified)
            .WithMessage("The server directory is an absolute path.")
            .Must(path => !File.Exists(path))
            .WithMessage("The server directory is a file.")
            .Must(path => !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
            .WithMessage("The server directory is not empty.")
            .When(r => !string.IsNullOrWhiteSpace(r.ServerDirectory));

        RuleFor(r => r.SuperUser).SetValidator(superUserValidator);
    }
}
