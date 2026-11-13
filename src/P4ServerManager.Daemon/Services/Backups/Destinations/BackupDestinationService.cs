using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using P4ServerManager.Backup.Repository;
using P4ServerManager.Backup.Targets;
using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Services.Backups.Destinations;

/// <summary>
/// The storage of a destination refused what was asked: unreachable, refused credentials, a repository the passphrase
/// does not open.
/// </summary>
public sealed class BackupDestinationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// A change of the destinations conflicts with their state: a name already taken, a destination still in use.
/// </summary>
public sealed class BackupDestinationConflictException(string message) : Exception(message);

/// <summary>
/// The storage target and the open repository of a destination, for one operation.
/// </summary>
public sealed class OpenedBackupDestination(BackupDestination destination, IBackupTarget target, BackupRepository repository)
    : IAsyncDisposable
{
    public BackupDestination Destination { get; } = destination;

    public IBackupTarget Target { get; } = target;

    public BackupRepository Repository { get; } = repository;

    public async ValueTask DisposeAsync()
    {
        Repository.Dispose();
        await Target.DisposeAsync();
    }
}

/// <summary>
/// Adds, changes, tests and opens the backup destinations. A destination always holds a repository: adding one creates
/// it on an empty storage, or attaches the one already there when the passphrase opens it.
/// </summary>
public sealed class BackupDestinationService(
    IDbContextFactory<DaemonDbContext> dbContextFactory,
    BackupTargetFactory targetFactory,
    BackupSecretProtector protector,
    DaemonPaths paths,
    TimeProvider timeProvider,
    ILogger<BackupDestinationService> logger)
{
    /// <summary>
    /// Options of the repositories the daemon opens.
    /// </summary>
    public BackupRepositoryOptions RepositoryOptions => new(paths.BackupTemporaryDirectory);

    public BackupDestinationConfiguration GetConfiguration(BackupDestination destination)
    {
        BackupDestinationCredentials credentials = destination.ProtectedCredentials is null
            ? new BackupDestinationCredentials()
            : BackupDestinationConfiguration.DeserializeCredentials(
                protector.UnprotectCredentials(destination.ProtectedCredentials));
        return BackupDestinationConfiguration.Deserialize(destination.Kind, destination.Settings, credentials);
    }

    public string GetLocation(BackupDestination destination)
    {
        try
        {
            return targetFactory.GetLocation(
                BackupDestinationConfiguration.Deserialize(destination.Kind, destination.Settings, new BackupDestinationCredentials()));
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <exception cref="BackupDestinationConflictException">The name is taken.</exception>
    /// <exception cref="BackupDestinationException">The storage cannot be reached, or holds a repository the passphrase does not open.</exception>
    public async Task<BackupDestination> CreateAsync(
        CreateBackupDestinationRequest request,
        CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureNameIsFreeAsync(db, request.Name, null, cancellationToken);

        BackupDestinationConfiguration configuration = new(
            request.Kind,
            request.Local,
            request.S3,
            request.Gcs,
            request.Azure,
            request.Ftp,
            request.Credentials ?? new BackupDestinationCredentials());
        Guid repositoryId = await OpenOrInitializeAsync(configuration, request.Passphrase, cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();
        BackupDestination destination = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Kind = request.Kind,
            Settings = configuration.SerializeSettings(),
            ProtectedCredentials = ProtectCredentials(configuration.Credentials),
            ProtectedPassphrase = protector.ProtectPassphrase(request.Passphrase),
            RepositoryId = repositoryId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.BackupDestinations.Add(destination);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Added backup destination {Name} ({Kind})", destination.Name, destination.Kind);
        return destination;
    }

    /// <exception cref="KeyNotFoundException">No destination has this identifier.</exception>
    /// <exception cref="BackupDestinationConflictException">The name is taken.</exception>
    /// <exception cref="BackupDestinationException">The storage cannot be reached, or holds another repository.</exception>
    public async Task<BackupDestination> UpdateAsync(
        Guid id,
        UpdateBackupDestinationRequest request,
        CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        BackupDestination destination = await db.BackupDestinations.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Backup destination {id} does not exist.");
        await EnsureNameIsFreeAsync(db, request.Name, id, cancellationToken);

        bool hasSettingsOfKind = destination.Kind switch
        {
            BackupDestinationKind.Local => request.Local is not null,
            BackupDestinationKind.S3 or BackupDestinationKind.S3Compatible => request.S3 is not null,
            BackupDestinationKind.GoogleCloudStorage => request.Gcs is not null,
            BackupDestinationKind.AzureBlob => request.Azure is not null,
            BackupDestinationKind.Ftp => request.Ftp is not null,
            _ => false,
        };
        if (!hasSettingsOfKind)
        {
            throw new BackupDestinationException(
                $"Backup destination {destination.Name} is of kind {destination.Kind}: give the settings of this kind.");
        }

        BackupDestinationConfiguration current = GetConfiguration(destination);
        BackupDestinationConfiguration configuration = new(
            destination.Kind,
            request.Local,
            request.S3,
            request.Gcs,
            request.Azure,
            request.Ftp,
            request.Credentials ?? current.Credentials);
        string passphrase = protector.UnprotectPassphrase(destination.ProtectedPassphrase);
        Guid repositoryId = await OpenOrInitializeAsync(configuration, passphrase, cancellationToken);

        destination.Name = request.Name;
        destination.Settings = configuration.SerializeSettings();
        destination.ProtectedCredentials = ProtectCredentials(configuration.Credentials);
        destination.RepositoryId = repositoryId;
        destination.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Changed backup destination {Name}", destination.Name);
        return destination;
    }

    /// <summary>
    /// Removes a destination from the daemon. Its repository and its backups stay on the storage.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No destination has this identifier.</exception>
    /// <exception cref="BackupDestinationConflictException">An instance still backs up to it.</exception>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        BackupDestination destination = await db.BackupDestinations
            .Include(d => d.Instances)
            .ThenInclude(i => i.Instance)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Backup destination {id} does not exist.");
        if (destination.Instances.Count > 0)
        {
            string names = string.Join(", ", destination.Instances.Select(i => i.Instance?.Name).Order());
            throw new BackupDestinationConflictException(
                $"Backup destination {destination.Name} is used by {names}. Remove it from these instances first.");
        }

        db.BackupDestinations.Remove(destination);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Removed backup destination {Name}", destination.Name);
    }

    /// <summary>
    /// Writes an object outside the repository, reads it back and deletes it.
    /// </summary>
    public async Task<BackupDestinationTestResult> TestAsync(
        BackupDestination destination,
        CancellationToken cancellationToken)
    {
        IBackupTarget? target = null;
        IReadOnlyList<string> capabilities = [];
        try
        {
            target = targetFactory.Create(GetConfiguration(destination));
            capabilities = Enum.GetValues<TargetCapabilities>()
                .Where(c => c != TargetCapabilities.None && target.Capabilities.HasFlag(c))
                .Select(c => c.ToString())
                .ToList();

            string key = $"tests/probe-{Guid.NewGuid():N}";
            byte[] content = RandomNumberGenerator.GetBytes(1024);
            await target.PutAsync(key, new MemoryStream(content), new BackupPutOptions(), cancellationToken);
            try
            {
                await using Stream stream = await target.OpenReadAsync(key, cancellationToken);
                using MemoryStream read = new();
                await stream.CopyToAsync(read, cancellationToken);
                if (!read.ToArray().AsSpan().SequenceEqual(content))
                {
                    return new BackupDestinationTestResult(false, "The object read back differs from the one written.", capabilities);
                }
            }
            finally
            {
                await target.DeleteAsync(key, cancellationToken);
            }

            if (!await BackupRepository.ExistsAsync(target, cancellationToken))
            {
                return new BackupDestinationTestResult(false, "The storage no longer holds the backup repository.", capabilities);
            }
            return new BackupDestinationTestResult(true, null, capabilities);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Test of backup destination {Name} failed", destination.Name);
            return new BackupDestinationTestResult(false, exception.Message, capabilities);
        }
        finally
        {
            if (target is not null)
            {
                await target.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Opens the repository of a destination with its stored passphrase.
    /// </summary>
    /// <exception cref="BackupDestinationException">The storage cannot be reached, or the repository is not the one of the destination.</exception>
    public async Task<OpenedBackupDestination> OpenAsync(
        BackupDestination destination,
        CancellationToken cancellationToken)
    {
        IBackupTarget target = targetFactory.Create(GetConfiguration(destination));
        try
        {
            string passphrase = protector.UnprotectPassphrase(destination.ProtectedPassphrase);
            BackupRepository repository = await BackupRepository.OpenAsync(target, passphrase, RepositoryOptions, cancellationToken);
            if (repository.RepositoryId != destination.RepositoryId)
            {
                repository.Dispose();
                throw new BackupDestinationException(
                    $"{target.Location} holds another repository than the one of destination {destination.Name}.");
            }
            return new OpenedBackupDestination(destination, target, repository);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await target.DisposeAsync();
            throw exception switch
            {
                BackupDestinationException => exception,
                _ => new BackupDestinationException(
                    $"Backup destination {destination.Name} cannot be opened: {exception.Message}",
                    exception),
            };
        }
    }

    /// <summary>
    /// Empties the folder where the objects are encrypted before they are uploaded: what a previous daemon left there.
    /// </summary>
    public void CleanTemporaryDirectory()
    {
        try
        {
            if (Directory.Exists(paths.BackupTemporaryDirectory))
            {
                Directory.Delete(paths.BackupTemporaryDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not empty {Directory}", paths.BackupTemporaryDirectory);
        }
    }

    private async Task<Guid> OpenOrInitializeAsync(
        BackupDestinationConfiguration configuration,
        string passphrase,
        CancellationToken cancellationToken)
    {
        IBackupTarget target;
        try
        {
            target = targetFactory.Create(configuration);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or FormatException)
        {
            throw new BackupDestinationException(exception.Message, exception);
        }

        await using (target)
        {
            try
            {
                using BackupRepository repository = await BackupRepository.ExistsAsync(target, cancellationToken)
                    ? await BackupRepository.OpenAsync(target, passphrase, RepositoryOptions, cancellationToken)
                    : await BackupRepository.InitializeAsync(target, passphrase, RepositoryOptions, cancellationToken);
                return repository.RepositoryId;
            }
            catch (BackupRepositoryPassphraseException exception)
            {
                throw new BackupDestinationException(
                    $"{target.Location} already holds a backup repository, and the passphrase does not open it.",
                    exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new BackupDestinationException($"{target.Location} cannot be used: {exception.Message}", exception);
            }
        }
    }

    private string? ProtectCredentials(BackupDestinationCredentials credentials)
    {
        return BackupDestinationConfiguration.HasAny(credentials)
            ? protector.ProtectCredentials(BackupDestinationConfiguration.SerializeCredentials(credentials))
            : null;
    }

    private static async Task EnsureNameIsFreeAsync(
        DaemonDbContext db,
        string name,
        Guid? exceptId,
        CancellationToken cancellationToken)
    {
        if (await db.BackupDestinations.AnyAsync(d => d.Name == name && d.Id != exceptId, cancellationToken))
        {
            throw new BackupDestinationConflictException($"A backup destination is already named {name}.");
        }
    }
}
