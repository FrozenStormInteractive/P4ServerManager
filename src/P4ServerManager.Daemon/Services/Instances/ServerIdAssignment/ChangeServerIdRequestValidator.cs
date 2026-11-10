using FluentValidation;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Instances.ServerIdAssignment;

/// <summary>
/// Checks a <see cref="ChangeServerIdRequest"/>. Whether the server ID differs from the current one depends on the
/// instance: the <see cref="InstanceSupervisor"/> checks it.
/// </summary>
public sealed class ChangeServerIdRequestValidator : AbstractValidator<ChangeServerIdRequest>
{
    public ChangeServerIdRequestValidator()
    {
        RuleFor(r => r.ServerId).MustBeServerId();
    }
}
