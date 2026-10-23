using P4ServerManager.Daemon.Database.Entities;
using P4ServerManager.Daemon.Services.Instances;

namespace P4ServerManager.Daemon.Contracts;

/// <summary>
/// A p4d server managed by the daemon.
/// </summary>
/// <param name="Id">Identifier of the instance.</param>
/// <param name="Name">Unique name, also used as the p4d server ID.</param>
/// <param name="Description">Optional free text.</param>
/// <param name="Port">TCP port p4d listens on.</param>
/// <param name="RootPath">Absolute path of the P4ROOT.</param>
/// <param name="Release">p4d release asked for at creation, for example <c>r25.1</c>.</param>
/// <param name="Version">Version of the pinned p4d build, for example <c>2025.1</c>. Null until resolved.</param>
/// <param name="Build">Build number of the pinned p4d build. Null until resolved.</param>
/// <param name="CaseSensitive">False when p4d runs case-insensitive (<c>-C1</c>).</param>
/// <param name="Unicode">True when the server runs in Unicode mode.</param>
/// <param name="State">Lifecycle state.</param>
/// <param name="RuntimeStatus">Whether the p4d process runs.</param>
/// <param name="LastError">Error of the last failed operation.</param>
/// <param name="CreatedAt">Creation date.</param>
public sealed record InstanceDto(
    Guid Id,
    string Name,
    string? Description,
    int Port,
    string RootPath,
    string Release,
    string? Version,
    string? Build,
    bool CaseSensitive,
    bool Unicode,
    P4ServerInstanceState State,
    InstanceRuntimeStatus RuntimeStatus,
    string? LastError,
    DateTimeOffset CreatedAt)
{
    public static InstanceDto From(P4ServerInstance instance, InstanceRuntimeStatus runtimeStatus)
    {
        return new InstanceDto(
            instance.Id,
            instance.Name,
            instance.Description,
            instance.Port,
            instance.RootPath,
            instance.Release,
            instance.Build?.Version,
            instance.Build?.BuildNumber,
            instance.CaseSensitive,
            instance.Unicode,
            instance.State,
            runtimeStatus,
            instance.LastError,
            instance.CreatedAt);
    }
}
