using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Reads from the database of an imported server whether it runs in Unicode mode. The case handling is not recorded
/// in the database: it comes from the import request.
/// </summary>
public sealed class ReadServerSettingsStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    InstanceNotifier notifier) : IOperationStep<ProvisioningContext>
{
    public string Key => "read-settings";

    public string Title => "Read the server settings";

    public async Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        ProcessResult dump = await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(instance.Build!),
                [.. P4ServerLauncher.GetRootArguments(instance), "-jd", "-", "db.config"],
                Timeout: ProcessRunnerExtensions.CommandTimeout),
            cancellationToken);

        instance.Unicode = ConfigDumpParser.IsUnicode(dump.StandardOutput);
        await context.Db.SaveChangesAsync(cancellationToken);
        await notifier.InstanceChangedAsync(instance);
        return instance.Unicode ? "Unicode mode" : "Not in Unicode mode";
    }
}
