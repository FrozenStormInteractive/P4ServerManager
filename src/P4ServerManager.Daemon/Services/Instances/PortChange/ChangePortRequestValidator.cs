using FluentValidation;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Instances.PortChange;

/// <summary>
/// Checks a <see cref="ChangePortRequest"/>. Whether another instance or program uses the port depends on the instance
/// being changed, whose own port is not free: <c>InstancesController</c> checks it.
/// </summary>
public sealed class ChangePortRequestValidator : AbstractValidator<ChangePortRequest>
{
    public ChangePortRequestValidator()
    {
        RuleFor(r => r.Port).MustBeInstancePort();
    }
}
