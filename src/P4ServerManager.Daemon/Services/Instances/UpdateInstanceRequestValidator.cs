using FluentValidation;

using P4ServerManager.Daemon.Contracts;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Checks an <see cref="UpdateInstanceRequest"/>.
/// </summary>
public sealed class UpdateInstanceRequestValidator : AbstractValidator<UpdateInstanceRequest>
{
    public UpdateInstanceRequestValidator()
    {
        RuleFor(r => r.AutoStart)
            .NotNull()
            .WithMessage("Give at least one setting to change.");
    }
}
