namespace P4ServerManager.Contracts;

/// <summary>
/// New TCP port of the p4d of an instance.
/// </summary>
public sealed record ChangePortRequest
{
    /// <summary>
    /// TCP port p4d listens on, between 1024 and 65535, that no other instance uses and that is free on the machine.
    /// </summary>
    public int Port { get; init; }
}
