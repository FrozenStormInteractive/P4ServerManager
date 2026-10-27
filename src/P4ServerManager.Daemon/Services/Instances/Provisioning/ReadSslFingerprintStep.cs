using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Reads the fingerprint of the certificate an imported server already has, in the <c>ssl</c> folder of its root, and
/// keeps it for the clients to check. Only part of the import of a server that uses SSL.
/// </summary>
public sealed class ReadSslFingerprintStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    InstanceNotifier notifier) : IOperationStep<ProvisioningContext>
{
    public string Key => "read-ssl-fingerprint";

    public string Title => "Read the SSL fingerprint";

    public bool IsIncluded(ProvisioningContext context)
    {
        return context.Instance.Ssl;
    }

    public async Task<string?> RunAsync(
        ProvisioningContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        ProcessResult fingerprint = await processRunner.RunCheckedAsync(
            new ProcessCommand(
                buildCache.GetP4dPath(instance.Build!),
                [.. P4ServerLauncher.GetRootArguments(instance), "-Gf"],
                null,
                P4ServerLauncher.GetEnvironment(instance),
                ProcessRunnerExtensions.CommandTimeout),
            cancellationToken);

        instance.SslFingerprint = SslFingerprintParser.Parse(fingerprint.StandardOutput)
            ?? throw new InstanceOperationException(
                $"p4d did not print the fingerprint of the certificate: {fingerprint.StandardOutput.Trim()}");
        await context.Db.SaveChangesAsync(cancellationToken);
        await notifier.InstanceChangedAsync(instance);
        return instance.SslFingerprint;
    }
}
