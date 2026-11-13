using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
using P4ServerManager.Daemon.Services.Operations;

namespace P4ServerManager.Daemon.Services.Backups.Restore.Steps;

/// <summary>
/// Gives the new instance its SSL certificate: the one of the point when it has one, which the clients already trust,
/// otherwise a new one. Only for an instance that uses SSL.
/// </summary>
public sealed class RestoreSslStep(
    GenerateSslCertificateStep generateCertificate,
    ReadSslFingerprintStep readFingerprint) : IOperationStep<NewInstanceRestoreContext>
{
    public string Key => "restore-ssl";

    public string Title => "Set up SSL";

    public bool IsIncluded(NewInstanceRestoreContext context)
    {
        return context.Instance.Ssl;
    }

    public Task<string?> RunAsync(
        NewInstanceRestoreContext context,
        IProgress<OperationStepProgress> progress,
        CancellationToken cancellationToken)
    {
        string sslDirectory = ServerLayout.GetSslDirectory(context.Instance.RootPath);
        bool restored = File.Exists(Path.Combine(sslDirectory, "certificate.txt"))
            && File.Exists(Path.Combine(sslDirectory, "privatekey.txt"));
        return restored
            ? readFingerprint.RunAsync(context, progress, cancellationToken)
            : generateCertificate.RunAsync(context, progress, cancellationToken);
    }
}
