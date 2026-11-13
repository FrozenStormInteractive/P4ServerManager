namespace P4ServerManager.Backup.Targets;

/// <summary>
/// Rules of the keys of the objects: segments separated by <c>/</c>, made of ASCII letters, digits, <c>.</c>,
/// <c>_</c> and <c>-</c>, never starting with a dot. Every target can store such a key as it is, as a file name or an
/// object name, and a target can keep its temporary files apart with a leading dot.
/// </summary>
public static class BackupObjectKey
{
    /// <exception cref="ArgumentException">The key breaks the rules.</exception>
    public static void Validate(string key)
    {
        if (!IsValid(key))
        {
            throw new ArgumentException($"'{key}' is not a valid object key.", nameof(key));
        }
    }

    public static bool IsValid(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 512)
        {
            return false;
        }

        foreach (string segment in key.Split('/'))
        {
            if (segment.Length == 0 || segment[0] == '.')
            {
                return false;
            }
            foreach (char c in segment)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Validates a prefix of keys: empty, or a key followed or not by <c>/</c>, or the start of a segment.
    /// </summary>
    /// <exception cref="ArgumentException">The prefix cannot start any valid key.</exception>
    public static void ValidatePrefix(string prefix)
    {
        if (prefix.Length == 0)
        {
            return;
        }

        string trimmed = prefix.EndsWith('/') ? prefix[..^1] : prefix;
        if (!IsValid(trimmed))
        {
            throw new ArgumentException($"'{prefix}' is not a valid prefix of object keys.", nameof(prefix));
        }
    }
}
