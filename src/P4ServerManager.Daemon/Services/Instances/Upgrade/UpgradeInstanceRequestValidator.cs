using FluentValidation;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

/// <summary>
/// Checks an <see cref="UpgradeInstanceRequest"/> against the p4d builds. Whether the release or the build is older
/// than the one of the instance is checked by the <see cref="InstanceSupervisor"/>: the instance is not part of the
/// body.
/// </summary>
public sealed class UpgradeInstanceRequestValidator : AbstractValidator<UpgradeInstanceRequest>
{
    public UpgradeInstanceRequestValidator(IP4ServerReleaseCatalog releaseCatalog, DaemonDbContext db)
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(r => r.Release).MustBeAvailableRelease(releaseCatalog, db);
        RuleFor(r => r.Build!)
            .MustBeArchivedBuild(db, r => r.Release)
            .When(r => r.Build is not null);
    }
}
