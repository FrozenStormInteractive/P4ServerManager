namespace P4ServerManager.Contracts;

/// <summary>
/// Settings of an instance that can change after its creation. A missing setting is left as it is.
/// </summary>
public sealed record UpdateInstanceRequest
{
    /// <summary>
    /// Unique name: a letter, then up to 31 letters, digits, '-' or '_'. Only the name in the daemon changes: the p4d
    /// server keeps its server ID and the prefix of its journals.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Free text. An empty text removes the description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// True to start p4d when the daemon starts.
    /// </summary>
    public bool? AutoStart { get; init; }
}
