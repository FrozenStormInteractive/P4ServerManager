using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Reads from the database of an imported server whether it runs in Unicode mode, and its server ID from its root. The
/// case handling is not recorded in the database: it comes from the import request.
/// </summary>
public sealed class ReadServerSettingsStep(
    ServerConfigurationReader configurationReader,
    InstanceNotifier notifier) : IOperationStep<IInstanceOperationContext>
{
    public string Key => "read-settings";

    public string Title => "Read the server settings";

    public async Task<string?> RunAsync(
        IInstanceOperationContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        ServerConfiguration configuration = await configurationReader.ReadAsync(instance, cancellationToken);

        instance.Unicode = configuration.IsUnicode;
        await context.Db.SaveChangesAsync(cancellationToken);
        await notifier.InstanceChangedAsync(instance);
        string mode = instance.Unicode ? "Unicode mode" : "Not in Unicode mode";
        return configuration.ServerId is null
            ? $"{mode}, no server ID"
            : $"{mode}, server ID {configuration.ServerId}";
    }
}
