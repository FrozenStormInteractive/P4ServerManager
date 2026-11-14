using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.FileSystem;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Import;

namespace P4ServerManager.Daemon.Contracts;

/// <summary>
/// Turns the types of the daemon into the types of the API.
/// </summary>
public static class ContractMappings
{
    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    /// <param name="runtimeStatus">Whether its p4d runs.</param>
    /// <param name="cancellationToken">Aborts the read of the server ID.</param>
    /// <remarks>
    /// The server ID is read from the <c>server.id</c> file of the root each time, so that a change made outside the
    /// daemon shows at once.
    /// </remarks>
    public static async Task<InstanceDto> ToDtoAsync(
        this P4ServerInstance instance,
        InstanceRuntimeStatus runtimeStatus,
        CancellationToken cancellationToken)
    {
        string? serverId = await ServerLayout.TryReadServerIdAsync(instance.RootPath, cancellationToken);
        return instance.ToDto(runtimeStatus, serverId);
    }

    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    /// <param name="runtimeStatus">Whether its p4d runs.</param>
    /// <param name="serverId">Server ID of p4d, or null when unknown.</param>
    public static InstanceDto ToDto(this P4ServerInstance instance, InstanceRuntimeStatus runtimeStatus, string? serverId)
    {
        return new InstanceDto(
            instance.Id,
            instance.Name,
            serverId,
            instance.Description,
            instance.Port,
            instance.Ssl,
            instance.SslFingerprint,
            instance.RootPath,
            instance.Release,
            instance.Build?.Version,
            instance.Build?.BuildNumber,
            instance.CaseSensitive,
            instance.Unicode,
            instance.AutoStart,
            instance.MaintenanceMode,
            instance.State,
            runtimeStatus,
            instance.LastError,
            instance.SuperUser is { } superUser
                ? new SuperUserDto(superUser.UserName, superUser.Status, superUser.CheckedAt, superUser.LastError)
                : null,
            instance.CreatedAt);
    }

    /// <param name="build">The build, with its <see cref="P4ServerBuild.Instances"/> loaded.</param>
    public static BuildDto ToDto(this P4ServerBuild build)
    {
        return new BuildDto(
            build.Id,
            build.Release,
            build.Version,
            build.BuildNumber,
            build.Imported ? BuildSource.Imported : BuildSource.Ftp,
            build.Sha256,
            build.DownloadedAt,
            build.ArchiveSize,
            build.IsExtracted,
            build.Protected,
            build.Instances
                .OrderBy(i => i.Name)
                .Select(i => new BuildInstanceDto(i.Id, i.Name))
                .ToList());
    }

    /// <summary>
    /// A run that is not in progress, or one as it begins: only the runner knows the progress.
    /// </summary>
    /// <param name="run">The run, with its <see cref="HealthCheckRun.Findings"/> loaded.</param>
    public static HealthCheckRunDto ToDto(this HealthCheckRun run)
    {
        return new HealthCheckRunDto(
            run.Id,
            run.InstanceId,
            run.Status,
            run.Checks,
            run.P4dVersion,
            run.StartedAt,
            run.FinishedAt,
            run.Error,
            run.HasFixJournal,
            null,
            run.Findings.Select(f => new HealthCheckFindingDto(f.Check, f.Table, f.Message)).ToList(),
            run.ReportHidden);
    }

    public static BuildArchivePolicyDto ToDto(this BuildArchivePolicy policy)
    {
        return new BuildArchivePolicyDto
        {
            AutoDownload = policy.AutoDownload,
            CheckIntervalHours = policy.CheckIntervalHours,
            Scope = policy.Scope,
            Releases = policy.Releases.ToList(),
            KeepLatestPerRelease = policy.KeepLatestPerRelease,
        };
    }

    /// <param name="profile">The profile.</param>
    /// <param name="instanceId">The instance that uses its root, when it was imported already.</param>
    public static LegacyProfileDto ToDto(this LegacyProfile profile, Guid? instanceId)
    {
        return new LegacyProfileDto(
            profile.Id,
            profile.Name,
            profile.RootPath,
            profile.Port,
            profile.Ssl,
            profile.CaseSensitive,
            profile.Enabled,
            profile.IgnoredSettings,
            profile.Problems,
            instanceId);
    }

    /// <param name="listing">The folder and its subfolders.</param>
    public static DirectoryListingDto ToDto(this DirectoryListing listing)
    {
        return new DirectoryListingDto(
            DirectoryBrowser.ToApiPath(listing.Path),
            listing.Parent is null ? null : DirectoryBrowser.ToApiPath(listing.Parent),
            listing.Writable,
            listing.Ancestors.Select(ToDirectoryEntryDto).ToList(),
            listing.Directories.Select(ToDirectoryEntryDto).ToList(),
            listing.Roots.Select(ToDirectoryEntryDto).ToList());
    }

    private static DirectoryEntryDto ToDirectoryEntryDto(DirectoryEntry entry)
    {
        return new DirectoryEntryDto(
            DirectoryBrowser.ToApiPath(DirectoryBrowser.GetName(entry.Path)),
            DirectoryBrowser.ToApiPath(entry.Path),
            entry.Writable);
    }
}
