namespace P4ServerManager.Daemon.Services.Instances.Import;

/// <summary>
/// A p4dctl-ng server profile of the HelixCoreServer Synology package, read from the folder of the profiles to import.
/// </summary>
/// <param name="Id">File name of the profile in that folder.</param>
/// <param name="Name">Name of the server in the profile, if any.</param>
/// <param name="RootPath">Absolute P4ROOT of the server, if the profile has one.</param>
/// <param name="Port">TCP port from the P4PORT of the profile, if it has one.</param>
/// <param name="CaseSensitive">
/// False when the profile runs p4d with <c>-C1</c>. The database does not record it: it is only a suggestion.
/// </param>
/// <param name="Enabled">Whether p4dctl-ng started the server with the others.</param>
/// <param name="IgnoredSettings">Settings of the profile the daemon does not carry over, such as P4JOURNAL.</param>
/// <param name="Problems">Reasons the profile cannot be imported. Empty when it can.</param>
public sealed record LegacyProfile(
    string Id,
    string? Name,
    string? RootPath,
    int? Port,
    bool CaseSensitive,
    bool Enabled,
    IReadOnlyList<string> IgnoredSettings,
    IReadOnlyList<string> Problems)
{
    public bool CanImport => Problems.Count == 0;
}
