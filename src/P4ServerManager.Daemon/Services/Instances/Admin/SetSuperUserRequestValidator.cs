using FluentValidation;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Instances.Admin;

/// <summary>
/// Checks the form of an account given for an instance. Its password has no strength rule: it already exists in P4.
/// Whether the server accepts the account is checked by <see cref="SuperUserService"/>.
/// </summary>
public sealed class SetSuperUserRequestValidator : AbstractValidator<SetSuperUserRequest>
{
    public SetSuperUserRequestValidator()
    {
        RuleFor(r => r.UserName).MustBeUserName();

        RuleFor(r => r.Password)
            .NotEmpty()
            .WithMessage("The password is required.");
    }
}
