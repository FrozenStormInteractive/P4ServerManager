using System.Text.Json.Serialization;

namespace P4ServerManager.Backup.Repository;

/// <summary>
/// The <c>config</c> object at the root of a repository, in clear: what is needed to open it with its passphrase. The
/// master key is only there encrypted.
/// </summary>
internal sealed class RepositoryConfig
{
    public const string FormatName = "p4servermanager-backup";

    public const int CurrentVersion = 1;

    public required string Format { get; init; }

    public required int Version { get; init; }

    public required Guid RepositoryId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required RepositoryKeyDerivation Kdf { get; init; }

    public required RepositoryWrappedKey Key { get; init; }
}

/// <summary>
/// How the key that encrypts the master key is derived from the passphrase.
/// </summary>
internal sealed class RepositoryKeyDerivation
{
    public const string Pbkdf2Sha256 = "pbkdf2-sha256";

    public required string Algorithm { get; init; }

    public required int Iterations { get; init; }

    public required byte[] Salt { get; init; }
}

/// <summary>
/// The master key, encrypted with AES-256-GCM by the key derived from the passphrase. The identifier of the repository
/// is its associated data.
/// </summary>
internal sealed class RepositoryWrappedKey
{
    public const string Aes256Gcm = "aes-256-gcm";

    public required string Algorithm { get; init; }

    public required byte[] Nonce { get; init; }

    /// <summary>
    /// The encrypted key, followed by its tag.
    /// </summary>
    public required byte[] Ciphertext { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RepositoryConfig))]
internal sealed partial class RepositoryJsonContext : JsonSerializerContext;
