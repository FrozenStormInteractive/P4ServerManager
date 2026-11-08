namespace P4ServerManager.Contracts;

/// <summary>
/// A p4d server managed by the daemon.
/// </summary>
/// <param name="Id">Identifier of the instance.</param>
/// <param name="Name">
/// Unique name. A new server takes it as its p4d server ID, which stays the same if the instance is renamed.
/// </param>
/// <param name="Description">Optional free text.</param>
/// <param name="Port">TCP port p4d listens on.</param>
/// <param name="Ssl">True when p4d listens with SSL, on <c>ssl:</c> followed by the port.</param>
/// <param name="SslFingerprint">
/// Fingerprint of the certificate of the server, that clients check before they trust it. Null without SSL.
/// </param>
/// <param name="RootPath">Absolute path of the P4ROOT.</param>
/// <param name="Release">p4d release asked for at creation or at the last upgrade, for example <c>r25.1</c>.</param>
/// <param name="Version">Version of the pinned p4d build, for example <c>2025.1</c>. Null until resolved.</param>
/// <param name="Build">Build number of the pinned p4d build. Null until resolved.</param>
/// <param name="CaseSensitive">False when p4d runs case-insensitive (<c>-C1</c>).</param>
/// <param name="Unicode">True when the server runs in Unicode mode.</param>
/// <param name="AutoStart">True when the daemon starts p4d when it starts.</param>
/// <param name="MaintenanceMode">
/// True when p4d runs, or starts, in maintenance mode (<c>p4d -n</c>): it only allows the commands that do not need a
/// client workspace, such as administration commands.
/// </param>
/// <param name="State">Lifecycle state.</param>
/// <param name="RuntimeStatus">
/// Whether the p4d process runs, or is being started, stopped, restarted, upgraded or deleted.
/// </param>
/// <param name="LastError">Error of the last failed operation.</param>
/// <param name="SuperUser">
/// Super user account the daemon stores to administer the server, with the result of its last check. Null when none is
/// stored, as for an imported instance.
/// </param>
/// <param name="CreatedAt">Creation date.</param>
public sealed record InstanceDto(
    Guid Id,
    string Name,
    string? Description,
    int Port,
    bool Ssl,
    string? SslFingerprint,
    string RootPath,
    string Release,
    string? Version,
    string? Build,
    bool CaseSensitive,
    bool Unicode,
    bool AutoStart,
    bool MaintenanceMode,
    P4ServerInstanceState State,
    InstanceRuntimeStatus RuntimeStatus,
    string? LastError,
    SuperUserDto? SuperUser,
    DateTimeOffset CreatedAt);

/// <summary>
/// Lifecycle state of an instance. Whether p4d runs is not stored: the supervisor reads it from the pid file.
/// </summary>
public enum P4ServerInstanceState
{
    Provisioning,
    Ready,
    Failed,
}
