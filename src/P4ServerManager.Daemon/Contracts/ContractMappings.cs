using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances.Import;

namespace P4ServerManager.Daemon.Contracts;

/// <summary>
/// Turns the types of the daemon into the types of the API.
/// </summary>
public static class ContractMappings
{
    /// <param name="instance">The instance, with its <see cref="P4ServerInstance.Build"/> loaded.</param>
    /// <param name="runtimeStatus">Whether its p4d runs.</param>
    public static InstanceDto ToDto(this P4ServerInstance instance, InstanceRuntimeStatus runtimeStatus)
    {
        return new InstanceDto(
            instance.Id,
            instance.Name,
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
            instance.State,
            runtimeStatus,
            instance.LastError,
            instance.CreatedAt);
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
}
