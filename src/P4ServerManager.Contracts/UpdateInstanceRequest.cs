namespace P4ServerManager.Contracts;

/// <summary>
/// Settings of an instance that can change after its creation. A missing setting is left as it is.
/// </summary>
public sealed record UpdateInstanceRequest
{
    /// <summary>
    /// True to start p4d when the daemon starts.
    /// </summary>
    public bool? AutoStart { get; init; }
}
