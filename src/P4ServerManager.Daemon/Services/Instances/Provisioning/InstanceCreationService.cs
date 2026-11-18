using Microsoft.EntityFrameworkCore;

using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Admin;

namespace P4ServerManager.Daemon.Services.Instances.Provisioning;

/// <summary>
/// Adds new instances: records them in the Provisioning state, with their super user, and queues their provisioning,
/// which runs in the background. Scoped: it saves through the context of the request.
/// </summary>
public sealed class InstanceCreationService(
    DaemonDbContext db,
    ProvisioningQueue queue,
    DaemonPaths paths,
    SuperUserPasswordProtector passwordProtector,
    InstanceNotifier notifier,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Creates an instance with recommended configuration.
    /// </summary>
    /// <param name="request">Settings of the instance, checked by <see cref="CreateInstanceRequestValidator"/>.</param>
    /// <param name="cancellationToken">Aborts the creation before the instance is saved.</param>
    /// <returns>The instance, saved and queued.</returns>
    /// <exception cref="InstanceConflictException">Another instance took the name or the port in the meantime.</exception>
    public async Task<P4ServerInstance> CreateAsync(CreateInstanceRequest request, CancellationToken cancellationToken)
    {
        // Checked by the validator. Without a build, the provisioning pins the latest one.
        Guid? buildId = request.Build is null
            ? null
            : await db.Builds
                .Where(b => b.Release == request.Release && b.BuildNumber == request.Build)
                .Select(b => (Guid?)b.Id)
                .FirstOrDefaultAsync(cancellationToken);

        P4ServerInstance instance = NewInstance(
            new NewInstanceSettings(
                request.Name,
                request.Description,
                request.Port,
                request.Ssl,
                request.ServerDirectory,
                request.AutoStart,
                request.SuperUser),
            request.Release,
            buildId,
            request.CaseSensitive,
            request.Unicode);
        await AddAsync(instance, request.SuperUser, ProvisioningKind.Create, restore: null, [], cancellationToken);
        return instance;
    }

    /// <summary>
    /// A new instance in the Provisioning state, not saved yet. Its super user is stored now and checked once the
    /// server is provisioned.
    /// </summary>
    /// <param name="settings">What the request sets.</param>
    /// <param name="release">Its p4d release.</param>
    /// <param name="buildId">Its build in the archive, or null for the provisioning to pin the latest one.</param>
    /// <param name="caseSensitive">Its case handling.</param>
    /// <param name="unicode">Its Unicode mode.</param>
    public P4ServerInstance NewInstance(
        NewInstanceSettings settings,
        string release,
        Guid? buildId,
        bool caseSensitive,
        bool unicode)
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset now = timeProvider.GetUtcNow();
        return new P4ServerInstance
        {
            Id = id,
            Name = settings.Name,
            Description = string.IsNullOrWhiteSpace(settings.Description) ? null : settings.Description.Trim(),
            Port = settings.Port,
            Ssl = settings.Ssl,
            RootPath = string.IsNullOrWhiteSpace(settings.ServerDirectory)
                ? paths.GetDefaultRootPath(settings.Name)
                : ServerLayout.GetRootPath(Path.GetFullPath(settings.ServerDirectory)),
            Release = release,
            BuildId = buildId,
            CaseSensitive = caseSensitive,
            Unicode = unicode,
            AutoStart = settings.AutoStart,
            State = P4ServerInstanceState.Provisioning,
            SuperUser = new InstanceSuperUser
            {
                UserName = settings.SuperUser.Name,
                ProtectedPassword = passwordProtector.Protect(settings.SuperUser.Password),
                Status = SuperUserStatus.Unchecked,
                UpdatedAt = now,
            },
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Saves a new instance, with the records that go with it, then queues its provisioning and tells the clients.
    /// </summary>
    /// <param name="instance">The instance, from <see cref="NewInstance"/>.</param>
    /// <param name="superUser">Its super user, which the provisioning creates or checks.</param>
    /// <param name="kind">What the provisioning does.</param>
    /// <param name="restore">The restore the provisioning runs, for <see cref="ProvisioningKind.Restore"/>.</param>
    /// <param name="related">Records saved with the instance, in the same transaction.</param>
    /// <param name="cancellationToken">Aborts the save.</param>
    /// <exception cref="InstanceConflictException">Another instance took the name or the port in the meantime.</exception>
    public async Task AddAsync(
        P4ServerInstance instance,
        SuperUserRequest superUser,
        ProvisioningKind kind,
        RestoreProvisioning? restore,
        IReadOnlyList<object> related,
        CancellationToken cancellationToken)
    {
        db.Instances.Add(instance);
        db.AddRange(related);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request took the name or the port in the meantime.
            throw new InstanceConflictException("Another instance already uses this name or port.");
        }

        await queue.EnqueueAsync(
            new ProvisioningRequest(instance.Id, kind, new SuperUserCredentials(superUser.Name, superUser.Password), restore),
            cancellationToken);
        await notifier.InstanceChangedAsync(instance);
    }
}

/// <summary>
/// The settings of a new instance that any request to create one gives.
/// </summary>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its description, or null.</param>
/// <param name="Port">Its port.</param>
/// <param name="Ssl">Whether it serves SSL.</param>
/// <param name="ServerDirectory">The folder of its server, or null for the default one.</param>
/// <param name="AutoStart">Whether the daemon starts it.</param>
/// <param name="SuperUser">Its super user.</param>
public sealed record NewInstanceSettings(
    string Name,
    string? Description,
    int Port,
    bool Ssl,
    string? ServerDirectory,
    bool AutoStart,
    SuperUserRequest SuperUser);
