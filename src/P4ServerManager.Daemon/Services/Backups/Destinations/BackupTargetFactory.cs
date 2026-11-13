using P4ServerManager.Backup.Targets;
using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Services.Backups.Destinations;

/// <summary>
/// Finds the provider of a kind of destination.
/// </summary>
public sealed class BackupTargetFactory(IEnumerable<IBackupTargetProvider> providers)
{
    private readonly Dictionary<BackupDestinationKind, IBackupTargetProvider> providers = providers
        .SelectMany(p => p.Kinds.Select(k => (Kind: k, Provider: p)))
        .ToDictionary(p => p.Kind, p => p.Provider);

    public bool Supports(BackupDestinationKind kind)
    {
        return providers.ContainsKey(kind);
    }

    public string GetLocation(BackupDestinationConfiguration configuration)
    {
        return GetProvider(configuration.Kind).GetLocation(configuration);
    }

    public IBackupTarget Create(BackupDestinationConfiguration configuration)
    {
        return GetProvider(configuration.Kind).Create(configuration);
    }

    private IBackupTargetProvider GetProvider(BackupDestinationKind kind)
    {
        return providers.TryGetValue(kind, out IBackupTargetProvider? provider)
            ? provider
            : throw new NotSupportedException($"Destinations of kind {kind} are not supported.");
    }
}

/// <summary>
/// Builds the storage target of one or more kinds of destination. Each adapter registers its own.
/// </summary>
public interface IBackupTargetProvider
{
    IReadOnlyCollection<BackupDestinationKind> Kinds { get; }

    /// <summary>
    /// Where a destination stores its repository, such as <c>s3://bucket/prefix</c>. Never a credential.
    /// </summary>
    string GetLocation(BackupDestinationConfiguration configuration);

    IBackupTarget Create(BackupDestinationConfiguration configuration);
}

/// <summary>
/// Folders of the machine of the daemon.
/// </summary>
public sealed class LocalBackupTargetProvider : IBackupTargetProvider
{
    public IReadOnlyCollection<BackupDestinationKind> Kinds { get; } = [BackupDestinationKind.Local];

    public string GetLocation(BackupDestinationConfiguration configuration)
    {
        return configuration.Local!.Path;
    }

    public IBackupTarget Create(BackupDestinationConfiguration configuration)
    {
        return new LocalBackupTarget(configuration.Local!.Path);
    }
}
