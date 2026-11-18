using FluentValidation;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Instances;

/// <summary>
/// Checks a <see cref="ServerDirectoryPreviewRequest"/>: a well-formed name without a server directory, an absolute
/// server directory otherwise. Whether the name is free is left to the creation.
/// </summary>
public sealed class ServerDirectoryPreviewRequestValidator : AbstractValidator<ServerDirectoryPreviewRequest>
{
    public ServerDirectoryPreviewRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(r => r.Name!)
            .NotEmpty()
            .WithMessage("The name is required without a server directory.")
            .MustBeInstanceName()
            .When(r => string.IsNullOrWhiteSpace(r.ServerDirectory));

        RuleFor(r => r.ServerDirectory!)
            .Must(Path.IsPathFullyQualified)
            .WithMessage("The server directory is an absolute path.")
            .When(r => !string.IsNullOrWhiteSpace(r.ServerDirectory));
    }
}
