using FluentValidation;

using P4ServerManager.Daemon.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Import;

/// <summary>
/// Checks an <see cref="ImportLegacyProfileRequest"/> against the other instances, the p4d catalog and the machine.
/// The profile itself is checked by the controller: it is not part of the body.
/// </summary>
public sealed class ImportLegacyProfileRequestValidator : AbstractValidator<ImportLegacyProfileRequest>
{
    public ImportLegacyProfileRequestValidator(DaemonDbContext db, IP4ServerReleaseCatalog releaseCatalog)
    {
        // One error per field: the checks against the database or the machine only run on a well-formed value.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(r => r.Name).MustBeNewInstanceName(db);
        RuleFor(r => r.Description).MustBeInstanceDescription();
        // A port still in use often means the server of the profile still runs.
        RuleFor(r => r.Port).MustBeFreeInstancePort(db);
        RuleFor(r => r.Release).MustBeAvailableRelease(releaseCatalog);
    }
}
