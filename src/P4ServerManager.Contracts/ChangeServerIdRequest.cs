using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Contracts;

/// <summary>
/// New server ID of the p4d of an instance.
/// </summary>
public sealed record ChangeServerIdRequest
{
    /// <summary>
    /// Server ID: a letter, then up to 31 letters, digits, '-' or '_'.
    /// </summary>
    [Required]
    public string ServerId { get; init; } = null!;
}
