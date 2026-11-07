using FluentValidation;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Checks an <see cref="UpdateInstanceRequest"/>. Whether another instance has the name depends on the instance being
/// changed: <c>InstancesController</c> checks it.
/// </summary>
public sealed class UpdateInstanceRequestValidator : AbstractValidator<UpdateInstanceRequest>
{
    public UpdateInstanceRequestValidator()
    {
        RuleFor(r => r.AutoStart)
            .NotNull()
            .WithMessage("Give at least one setting to change.")
            .When(r => r.Name is null && r.Description is null);

        RuleFor(r => r.Name!)
            .MustBeInstanceName()
            .When(r => r.Name is not null);

        RuleFor(r => r.Description).MustBeInstanceDescription();
    }
}
