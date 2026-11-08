using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Steps;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Perforce.Parsing;
using P4ServerManager.Daemon.Services.Processes;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Generates the private key and the certificate of an instance that uses SSL, in the <c>ssl</c> folder of its root,
/// and keeps the fingerprint that clients check before they trust the server. Only part of the provisioning with SSL.
/// </summary>
public sealed class GenerateSslCertificateStep(
    IP4ServerBuildCache buildCache,
    IProcessRunner processRunner,
    InstanceNotifier notifier) : IOperationStep<IInstanceOperationContext>
{
    public string Key => "generate-ssl-certificate";

    public string Title => "Generate the SSL certificate";

    public async Task<string?> RunAsync(
        IInstanceOperationContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        P4ServerInstance instance = context.Instance;
        string p4dPath = buildCache.GetP4dPath(instance.Build!);
        IReadOnlyList<string> root = P4ServerLauncher.GetRootArguments(instance);
        IReadOnlyDictionary<string, string>? environment = P4ServerLauncher.GetEnvironment(instance);
        TimeSpan timeout = ProcessRunnerExtensions.CommandTimeout;

        await processRunner.RunCheckedAsync(
            new ProcessCommand(p4dPath, [.. root, "-Gc"], null, environment, timeout),
            cancellationToken);
        ProcessResult fingerprint = await processRunner.RunCheckedAsync(
            new ProcessCommand(p4dPath, [.. root, "-Gf"], null, environment, timeout),
            cancellationToken);

        instance.SslFingerprint = SslFingerprintParser.Parse(fingerprint.StandardOutput)
            ?? throw new InstanceOperationException(
                $"p4d did not print the fingerprint of the certificate: {fingerprint.StandardOutput.Trim()}");
        await context.Db.SaveChangesAsync(cancellationToken);
        await notifier.InstanceChangedAsync(instance);
        return instance.SslFingerprint;
    }
}
