using System.ComponentModel.DataAnnotations;

namespace P4ServerManager.Contracts;

// The request bodies are records with properties, not positional records: MVC keys the validation errors of a
// constructor parameter by its C# name, and those of a property by its JSON name.

/// <summary>
/// Settings of a new instance.
/// </summary>
public sealed record CreateInstanceRequest
{
    /// <summary>
    /// Unique name: a letter, then up to 31 letters, digits, '-' or '_'. Also the p4d server ID.
    /// </summary>
    [Required]
    public string Name { get; init; } = null!;

    /// <summary>
    /// Optional free text.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// p4d release from <c>GET /api/p4d/versions</c>, for example <c>r25.1</c>.
    /// </summary>
    [Required]
    public string Release { get; init; } = null!;

    /// <summary>
    /// TCP port p4d listens on, between 1024 and 65535.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// False to run p4d case-insensitive (<c>-C1</c>). It cannot be changed later.
    /// </summary>
    public bool CaseSensitive { get; init; }

    /// <summary>
    /// True to run the server in Unicode mode. It cannot be undone.
    /// </summary>
    public bool Unicode { get; init; }

    /// <summary>
    /// True to start p4d when the daemon starts. True when missing. It can be changed later.
    /// </summary>
    public bool AutoStart { get; init; } = true;

    /// <summary>
    /// True to listen with SSL (<c>ssl:</c> port). The daemon generates the private key and the certificate of the
    /// server. False when missing.
    /// </summary>
    public bool Ssl { get; init; }

    /// <summary>
    /// Absolute folder of the server, empty or missing. The P4ROOT is its <c>root</c> subfolder, next to the
    /// <c>journals</c>, <c>logs</c> and <c>archives</c> folders. Defaults to a folder of the instance.
    /// </summary>
    public string? ServerDirectory { get; init; }

    /// <summary>
    /// Super user created with the server, required: it is the only account that can administer it. Its password is
    /// only given to p4d and never stored.
    /// </summary>
    [Required]
    public SuperUserRequest SuperUser { get; init; } = null!;
}

/// <summary>
/// Super user of a new instance.
/// </summary>
public sealed record SuperUserRequest
{
    /// <summary>
    /// User name: a letter, then letters, digits, '.', '-' or '_'.
    /// </summary>
    [Required]
    public string Name { get; init; } = null!;

    /// <summary>
    /// Password: at least 8 characters, from at least two of the classes upper case, lower case and non-alphabetic.
    /// </summary>
    [Required]
    public string Password { get; init; } = null!;

    // Keeps the password out of logs that print the record.
    public override string ToString()
    {
        return $"SuperUserRequest {{ Name = {Name} }}";
    }
}
