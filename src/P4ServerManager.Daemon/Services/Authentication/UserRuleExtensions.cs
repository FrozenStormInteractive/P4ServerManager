using System.Text.RegularExpressions;

using FluentValidation;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;

namespace P4ServerManager.Daemon.Services.Authentication;

/// <summary>
/// Rules of the names and the passwords of the accounts of the web interface.
/// </summary>
public static partial class UserRuleExtensions
{
    /// <summary>
    /// A well-formed user name, whether an account has it or not.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustBeAccountName<T>(this IRuleBuilder<T, string> rule)
    {
        return rule
            .Matches(UserNamePattern())
            .WithMessage("The user name starts with a letter or a digit and has up to 64 letters, digits, '.', '-', "
                + "'_' or '@'.");
    }

    /// <summary>
    /// A well-formed user name that no account has, whatever its case.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustBeNewAccountName<T>(
        this IRuleBuilder<T, string> rule,
        DaemonDbContext db)
    {
        // The user names are compared with the NOCASE collation of their column.
        return rule
            .MustBeAccountName()
            .MustAsync(async (name, cancellationToken) =>
                !await db.Users.AnyAsync(u => u.UserName == name, cancellationToken))
            .WithMessage((_, name) => $"An account named {name} already exists.");
    }

    /// <summary>
    /// A password of the length <see cref="UserPassword"/> asks for.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MustBeAccountPassword<T>(this IRuleBuilder<T, string> rule)
    {
        return rule
            .Must(UserPassword.IsValid)
            .WithMessage(UserPassword.Requirement);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$")]
    private static partial Regex UserNamePattern();
}
