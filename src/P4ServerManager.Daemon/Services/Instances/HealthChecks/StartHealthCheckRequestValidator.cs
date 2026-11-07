using FluentValidation;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Instances.HealthChecks;

/// <summary>
/// Checks a <see cref="StartHealthCheckRequest"/>.
/// </summary>
public sealed class StartHealthCheckRequestValidator : AbstractValidator<StartHealthCheckRequest>
{
    public StartHealthCheckRequestValidator()
    {
        RuleFor(r => r.Checks).NotEmpty().WithMessage("Choose at least one check.");
    }
}
