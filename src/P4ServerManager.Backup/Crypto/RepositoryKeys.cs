using System.Security.Cryptography;

namespace P4ServerManager.Backup.Crypto;

/// <summary>
/// Keys of an open repository, derived from its master key: one encrypts the objects, through a key of their own, the
/// other names them.
/// </summary>
internal sealed class RepositoryKeys : IDisposable
{
    public const int KeySize = 32;

    private static readonly byte[] ObjectsInfo = "p4servermanager/backup/objects/v1"u8.ToArray();
    private static readonly byte[] NamesInfo = "p4servermanager/backup/names/v1"u8.ToArray();
    private static readonly byte[] ObjectKeyInfo = "p4servermanager/backup/object-key/v1"u8.ToArray();

    private readonly byte[] encryptionKey;
    private readonly byte[] namingKey;

    private RepositoryKeys(byte[] encryptionKey, byte[] namingKey)
    {
        this.encryptionKey = encryptionKey;
        this.namingKey = namingKey;
    }

    /// <param name="masterKey">The master key of the repository.</param>
    /// <param name="repositoryId">Identifier of the repository: two repositories never share a key.</param>
    public static RepositoryKeys Derive(ReadOnlySpan<byte> masterKey, Guid repositoryId)
    {
        byte[] salt = repositoryId.ToByteArray();
        byte[] encryptionKey = new byte[KeySize];
        byte[] namingKey = new byte[KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, encryptionKey, salt, ObjectsInfo);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, namingKey, salt, NamesInfo);
        return new RepositoryKeys(encryptionKey, namingKey);
    }

    /// <summary>
    /// Key that encrypts one object, from the random salt in its header.
    /// </summary>
    public byte[] DeriveObjectKey(ReadOnlySpan<byte> salt)
    {
        byte[] key = new byte[KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, encryptionKey, key, salt, ObjectKeyInfo);
        return key;
    }

    /// <summary>
    /// Name of the object that holds a content: a keyed hash of its SHA-256, so that the storage cannot tell whether
    /// it holds a content it knows.
    /// </summary>
    public string GetObjectName(ReadOnlySpan<byte> contentSha256)
    {
        return Convert.ToHexStringLower(HMACSHA256.HashData(namingKey, contentSha256));
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(encryptionKey);
        CryptographicOperations.ZeroMemory(namingKey);
    }
}
