using FluentValidation;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Builds;

/// <summary>
/// Checks a <see cref="DownloadBuildRequest"/>. Whether the Perforce FTP publishes the release is only known once the
/// download runs.
/// </summary>
public sealed class DownloadBuildRequestValidator : AbstractValidator<DownloadBuildRequest>
{
    public DownloadBuildRequestValidator()
    {
        RuleFor(r => r.Release).MustBeReleaseName();
    }
}

/// <summary>
/// Checks an <see cref="UpdateBuildRequest"/>.
/// </summary>
public sealed class UpdateBuildRequestValidator : AbstractValidator<UpdateBuildRequest>
{
    public UpdateBuildRequestValidator()
    {
        RuleFor(r => r.Protected)
            .NotNull()
            .WithMessage("Give at least one setting to change.");
    }
}

/// <summary>
/// Checks a <see cref="BuildArchivePolicyDto"/>.
/// </summary>
public sealed class BuildArchivePolicyDtoValidator : AbstractValidator<BuildArchivePolicyDto>
{
    public const int MinCheckIntervalHours = 1;
    public const int MaxCheckIntervalHours = 720;

    public BuildArchivePolicyDtoValidator()
    {
        RuleFor(p => p.CheckIntervalHours)
            .InclusiveBetween(MinCheckIntervalHours, MaxCheckIntervalHours)
            .WithMessage("The interval between two checks is between {From} and {To} hours.");

        RuleFor(p => p.Scope).IsInEnum();

        // Without the automatic download, the releases only matter to a check asked for by hand.
        RuleFor(p => p.Releases)
            .NotEmpty()
            .When(p => p.AutoDownload && p.Scope == BuildArchiveScope.SelectedReleases)
            .WithMessage("Select at least one release.");
        RuleForEach(p => p.Releases).MustBeReleaseName();

        RuleFor(p => p.KeepLatestPerRelease)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Keep at least one build per release.");
    }
}

/// <summary>
/// Checks the form of <c>POST /api/p4d/builds/import</c>.
/// </summary>
public sealed class ImportBuildFormValidator : AbstractValidator<ImportBuildForm>
{
    public ImportBuildFormValidator()
    {
        RuleFor(f => f.Package)
            .Null()
            .When(f => f.P4d is not null || f.P4 is not null)
            .WithMessage("Send either the package, or p4d and p4.");
        RuleFor(f => f.P4d)
            .NotNull()
            .When(f => f.Package is null)
            .WithMessage("Send p4d, or the package that holds it.");
        RuleFor(f => f.P4)
            .NotNull()
            .When(f => f.Package is null)
            .WithMessage("Send p4, or the package that holds it.");
    }
}

internal static class BuildRuleExtensions
{
    public static IRuleBuilderOptions<T, string> MustBeReleaseName<T>(this IRuleBuilder<T, string> rule)
    {
        return rule
            .Must(release => P4ServerRelease.TryParse(release, out _))
            .WithMessage((_, release) => $"{release} is not a p4d release such as r25.1.");
    }
}
