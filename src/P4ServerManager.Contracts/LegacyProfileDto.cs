namespace P4ServerManager.Contracts;

/// <summary>
/// A server profile of the HelixCoreServer Synology package (p4dctl-ng), waiting to be imported.
/// </summary>
/// <param name="Id">Identifier of the profile: its file name.</param>
/// <param name="Name">Name of the server in the profile, suggested as the name of the instance.</param>
/// <param name="RootPath">Absolute P4ROOT of the server. An imported instance keeps it.</param>
/// <param name="Port">TCP port of the server, if the profile gives one.</param>
/// <param name="CaseSensitive">
/// False when the profile ran p4d with <c>-C1</c>. The database does not record it: it is a suggestion to confirm.
/// </param>
/// <param name="AutoStart">Whether p4dctl-ng started the server with the others, suggested as the auto-start.</param>
/// <param name="IgnoredSettings">Settings of the profile an imported instance does not keep, such as P4JOURNAL.</param>
/// <param name="Problems">Why the profile cannot be imported. Empty when it can.</param>
/// <param name="InstanceId">The instance that uses the root of the profile, when it was imported already.</param>
public sealed record LegacyProfileDto(
    string Id,
    string? Name,
    string? RootPath,
    int? Port,
    bool CaseSensitive,
    bool AutoStart,
    IReadOnlyList<string> IgnoredSettings,
    IReadOnlyList<string> Problems,
    Guid? InstanceId);
