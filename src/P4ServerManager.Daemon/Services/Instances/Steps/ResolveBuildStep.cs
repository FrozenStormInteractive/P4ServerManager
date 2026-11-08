using System.Globalization;

using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Steps;

/// <summary>
/// Gets a p4d build: the requested one, extracted from the archive when needed, or the latest build of the release,
/// downloaded when the daemon does not have it yet. The build is handed to the next steps as <see cref="ResolvedBuild"/>,
/// and the instance is pinned to it when the context asks for it.
/// </summary>
public sealed class ResolveBuildStep(IP4ServerBuildCache buildCache, InstanceNotifier notifier)
    : IOperationStep<IBuildResolutionContext>
{
    /// <summary>
    /// The build this step got.
    /// </summary>
    public static readonly OperationValueKey<P4ServerBuild> ResolvedBuild = new("resolved-build");

    public string Key => "resolve-build";

    public string Title => "Get the p4d build";

    public async Task<string?> RunAsync(
        IBuildResolutionContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!P4ServerRelease.TryParse(context.ReleaseName, out P4ServerRelease release))
        {
            throw new InstanceOperationException($"{context.ReleaseName} is not a p4d release.");
        }

        IProgress<BuildProgress> buildProgress = ReportBuildProgress(progress);
        P4ServerBuild build = context.RequestedBuildId is { } buildId
            ? await buildCache.EnsureAsync(buildId, buildProgress, cancellationToken)
            : await buildCache.EnsureAsync(release, buildProgress, cancellationToken);
        context.Values.Set(ResolvedBuild, build);

        P4ServerInstance instance = context.Instance;
        if (context.PinResolvedBuild)
        {
            instance.BuildId = build.Id;
            await context.Db.SaveChangesAsync(cancellationToken);
            await context.Db.Entry(instance).Reference(i => i.Build).LoadAsync(cancellationToken);
            await notifier.InstanceChangedAsync(instance);
        }
        else if (build.Id == instance.BuildId)
        {
            return context.RequestedBuildId is null
                ? $"Already on p4d {build.Version}/{build.BuildNumber}, the latest build of {release}"
                : $"Already on p4d {build.Version}/{build.BuildNumber}";
        }
        return $"p4d {build.Version}/{build.BuildNumber}";
    }

    /// <summary>
    /// Reports how the cache gets a build ready, downloading, compressing or extracting it, as the progress of the step
    /// that gets it.
    /// </summary>
    internal static IProgress<BuildProgress> ReportBuildProgress(IProgress<OperationStepProgress> progress)
    {
        return new InlineProgress<BuildProgress>(p =>
        {
            string message = p.Percent is { } percent
                ? string.Create(CultureInfo.InvariantCulture, $"{p.Description} ({percent}%)")
                : p.Description;
            progress.Report(new OperationStepProgress(message, p.Percent));
        });
    }
}
