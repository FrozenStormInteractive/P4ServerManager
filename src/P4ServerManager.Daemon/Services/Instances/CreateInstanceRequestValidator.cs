using System.Text.RegularExpressions;

using FluentValidation;

using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Checks a <see cref="CreateInstanceRequest"/> against the other instances, the p4d catalog and the machine.
/// </summary>
public sealed class CreateInstanceRequestValidator : AbstractValidator<CreateInstanceRequest>
{
    public CreateInstanceRequestValidator(
        DaemonDbContext db,
        IP4ServerReleaseCatalog releaseCatalog,
        IValidator<SuperUserRequest> superUserValidator)
    {
        // One error per field: the checks against the database or the machine only run on a well-formed value.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(r => r.Name).MustBeNewInstanceName(db);
        RuleFor(r => r.Description).MustBeInstanceDescription();
        RuleFor(r => r.Port).MustBeFreeInstancePort(db);
        RuleFor(r => r.Release).MustBeAvailableRelease(releaseCatalog);

        RuleFor(r => r.RootPath!)
            .Must(Path.IsPathFullyQualified)
            .WithMessage("The root directory is an absolute path.")
            .Must(path => !File.Exists(path))
            .WithMessage("The root directory is a file.")
            .Must(path => !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
            .WithMessage("The root directory is not empty.")
            .When(r => !string.IsNullOrWhiteSpace(r.RootPath));

        RuleFor(r => r.SuperUser).SetValidator(superUserValidator);
    }
}

/// <summary>
/// Checks the super user of a new instance.
/// </summary>
public sealed partial class SuperUserRequestValidator : AbstractValidator<SuperUserRequest>
{
    public const int MinPasswordLength = 8;

    public SuperUserRequestValidator()
    {
        RuleFor(u => u.Name)
            .Matches(UserNamePattern())
            .WithMessage("The user name starts with a letter and has up to 64 letters, digits, '.', '-' or '_'.");

        RuleFor(u => u.Password)
            .MinimumLength(MinPasswordLength)
            .WithMessage("The password has at least {MinLength} characters.");
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex UserNamePattern();
}
