using System.Globalization;

using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Pins the instance to the latest build of its release, downloading it when the daemon does not have it yet, or
/// extracts the exact build it was created with.
/// </summary>
public sealed class ResolveBuildStep(IP4ServerBuildCache buildCache, InstanceNotifier notifier)
    : IOperationStep<ProvisioningContext>
{
    public string Key => "resolve-build";

    public string Title => "Get the p4d build";

    public async Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        if (!P4ServerRelease.TryParse(instance.Release, out P4ServerRelease release))
        {
            throw new InstanceOperationException($"{instance.Release} is not a p4d release.");
        }

        P4ServerBuild build = instance.BuildId is { } buildId
            ? await buildCache.EnsureAsync(buildId, ReportBuildProgress(progress), cancellationToken)
            : await buildCache.EnsureAsync(release, ReportBuildProgress(progress), cancellationToken);

        instance.BuildId = build.Id;
        await context.Db.SaveChangesAsync(cancellationToken);
        await context.Db.Entry(instance).Reference(i => i.Build).LoadAsync(cancellationToken);
        await notifier.InstanceChangedAsync(instance);
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
