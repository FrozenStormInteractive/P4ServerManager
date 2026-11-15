using FluentValidation;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;

namespace P4ServerManager.Daemon.Services.Authentication;

/// <summary>
/// Checks the first account. Whether an account exists already is checked by <see cref="UserService"/>.
/// </summary>
public sealed class SetupRequestValidator : AbstractValidator<SetupRequest>
{
    public SetupRequestValidator()
    {
        RuleFor(r => r.UserName).MustBeAccountName();
        RuleFor(r => r.Password).MustBeAccountPassword();
    }
}

/// <summary>
/// Checks that a sign-in gives a user name and a password. Their form is not checked: a wrong one fails like a wrong
/// password.
/// </summary>
public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.UserName).NotEmpty().WithMessage("The user name is required.");
        RuleFor(r => r.Password).NotEmpty().WithMessage("The password is required.");
    }
}

/// <summary>
/// Checks a change of the password of the signed-in user. Whether the current password is right is checked by
/// <see cref="UserService"/>.
/// </summary>
public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(r => r.CurrentPassword).NotEmpty().WithMessage("The current password is required.");
        RuleFor(r => r.NewPassword).MustBeAccountPassword();
    }
}

/// <summary>
/// Checks a new account against the other ones.
/// </summary>
public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator(DaemonDbContext db)
    {
        // One error per field: the check against the database only runs on a well-formed name.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(r => r.UserName).MustBeNewAccountName(db);
        RuleFor(r => r.Password).MustBeAccountPassword();
    }
}

/// <summary>
/// Checks a new password set on an account.
/// </summary>
public sealed class SetUserPasswordRequestValidator : AbstractValidator<SetUserPasswordRequest>
{
    public SetUserPasswordRequestValidator()
    {
        RuleFor(r => r.Password).MustBeAccountPassword();
    }
}
