using System.Globalization;

using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Pins the instance to the latest build of its release, downloading it when it is not cached yet.
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

        InlineProgress<BuildDownloadProgress> downloadProgress = new(p =>
        {
            string message = p.Percent is { } percent
                ? string.Create(CultureInfo.InvariantCulture, $"Downloading {p.FileName} ({percent}%)")
                : $"Downloading {p.FileName}";
            progress.Report(new OperationStepProgress(message, p.Percent));
        });
        P4ServerBuild build = await buildCache.EnsureAsync(release, downloadProgress, cancellationToken);

        instance.BuildId = build.Id;
        await context.Db.SaveChangesAsync(cancellationToken);
        await context.Db.Entry(instance).Reference(i => i.Build).LoadAsync(cancellationToken);
        await notifier.InstanceChangedAsync(instance);
        return $"p4d {build.Version}/{build.BuildNumber}";
    }
}
