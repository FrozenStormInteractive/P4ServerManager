using System.Globalization;

using P4ServerManager.Daemon.Services.Instances.Admin;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Sets the security level and the configurables of <see cref="RecommendedConfiguration"/>. A configurable p4d refuses
/// does not stop the provisioning: an older release does not know them all.
/// </summary>
public sealed class ConfigureServerStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    DaemonPaths paths,
    ILogger<ConfigureServerStep> logger) : IOperationStep<ISuperUserContext>
{
    public string Key => "configure-server";

    public string Title => "Apply the recommended configuration";

    public async Task<string?> RunAsync(
        ISuperUserContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4AdminSession session = await P4AdminSession.OpenAsync(
            buildCache,
            processRunner,
            paths,
            logger,
            context,
            cancellationToken);
        IReadOnlyList<(string Name, string Value)> configurables =
            RecommendedConfiguration.GetConfigurables(context.Instance);

        return await session.RunLoggedInAsync(async () =>
        {
            await session.RunAsync(
                ["configure", "set", $"security={RecommendedConfiguration.SecurityLevel}"],
                cancellationToken: cancellationToken);

            List<string> skipped = [];
            foreach ((string name, string value) in configurables)
            {
                ProcessResult result = await session.TryRunAsync(
                    ["configure", "set", $"{name}={value}"],
                    cancellationToken: cancellationToken);
                if (!result.Succeeded)
                {
                    logger.LogWarning("Could not set {Configurable} on instance {Name}: {Error}",
                        name, context.Instance.Name, result.ErrorText);
                    skipped.Add(name);
                }
            }

            string summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Security level {RecommendedConfiguration.SecurityLevel}, {configurables.Count - skipped.Count} configurables set");
            return skipped.Count == 0 ? summary : $"{summary}, skipped: {string.Join(", ", skipped)}";
        }, cancellationToken);
    }
}
