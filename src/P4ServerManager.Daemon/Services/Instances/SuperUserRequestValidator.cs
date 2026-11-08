using FluentValidation;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Checks the super user of a new instance.
/// </summary>
public sealed class SuperUserRequestValidator : AbstractValidator<SuperUserRequest>
{
    public SuperUserRequestValidator()
    {
        RuleFor(u => u.Name).MustBeUserName();

        RuleFor(u => u.Password)
            .Must(SuperUserPassword.IsStrong)
            .WithMessage(SuperUserPassword.Requirement);
    }
}
