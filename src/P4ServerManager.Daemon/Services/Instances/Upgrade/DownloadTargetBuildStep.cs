using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;

namespace P4ServerManager.Daemon.Services.Instances.Upgrade;

/// <summary>
/// Gets the latest build of the target release, downloading it when it is not cached yet. p4d still runs meanwhile.
/// When the instance already runs that build, the upgrade has nothing left to do: the next steps are skipped.
/// </summary>
public sealed class DownloadTargetBuildStep(IP4ServerBuildCache buildCache) : IOperationStep<UpgradeContext>
{
    public string Key => "resolve-build";

    public string Title => "Get the p4d build";

    public async Task<string?> RunAsync(
        UpgradeContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerBuild build = await buildCache.EnsureAsync(
            context.Release,
            ResolveBuildStep.ReportDownload(progress),
            cancellationToken);
        if (build.Id == context.PreviousBuild.Id)
        {
            context.UpToDate = true;
            return $"Already on p4d {build.Version}/{build.BuildNumber}, the latest build of {context.Release}";
        }

        context.TargetBuild = build;
        return $"p4d {build.Version}/{build.BuildNumber}";
    }
}
