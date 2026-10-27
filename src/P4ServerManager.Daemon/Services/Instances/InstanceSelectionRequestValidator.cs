using FluentValidation;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Checks an <see cref="InstanceSelectionRequest"/>: either a list of instances, or all of them.
/// </summary>
public sealed class InstanceSelectionRequestValidator : AbstractValidator<InstanceSelectionRequest>
{
    public InstanceSelectionRequestValidator()
    {
        RuleFor(r => r.InstanceIds)
            .NotEmpty()
            .When(r => !r.All)
            .WithMessage("List at least one instance, or select all of them.");
        RuleFor(r => r.InstanceIds)
            .Empty()
            .When(r => r.All)
            .WithMessage("Do not list instances when all of them are selected.");
    }
}
