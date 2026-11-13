namespace P4ServerManager.Backup.Targets;

/// <summary>
/// The prefix under which a remote target keeps its objects, such as a folder of a bucket.
/// </summary>
public sealed class BackupTargetPath
{
    /// <summary>
    /// The folder, ending with a slash, or empty.
    /// </summary>
    public string Prefix { get; }

    /// <param name="prefix">The folder, without or with slashes around, or null or empty for the root.</param>
    public BackupTargetPath(string? prefix)
    {
        string trimmed = (prefix ?? string.Empty).Trim('/');
        if (trimmed.Length > 0)
        {
            BackupObjectKey.Validate(trimmed);
        }
        Prefix = trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>
    /// The remote name of an object.
    /// </summary>
    public string ToRemote(string key)
    {
        BackupObjectKey.Validate(key);
        return Prefix + key;
    }

    /// <summary>
    /// The remote start of the names of a listing.
    /// </summary>
    public string ToRemotePrefix(string keyPrefix)
    {
        BackupObjectKey.ValidatePrefix(keyPrefix);
        return Prefix + keyPrefix;
    }

    /// <summary>
    /// The key of a remote name, or null when the name is not under the folder or is not a valid key.
    /// </summary>
    public string? ToKey(string remote)
    {
        if (!remote.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }
        
        string key = remote[Prefix.Length..];
        return BackupObjectKey.IsValid(key) ? key : null;
    }
}
