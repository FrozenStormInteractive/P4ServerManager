using FluentValidation;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

/// <summary>
/// Checks an <see cref="UpgradeInstanceRequest"/> against the p4d catalog. Whether the release is older than the one of
/// the instance is checked by the <see cref="InstanceSupervisor"/>: the instance is not part of the body.
/// </summary>
public sealed class UpgradeInstanceRequestValidator : AbstractValidator<UpgradeInstanceRequest>
{
    public UpgradeInstanceRequestValidator(IP4ServerReleaseCatalog releaseCatalog)
    {
        RuleFor(r => r.Release).MustBeAvailableRelease(releaseCatalog);
    }
}
