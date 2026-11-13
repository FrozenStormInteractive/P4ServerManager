using System.Buffers.Binary;

namespace P4ServerManager.Backup.Crypto;

/// <summary>
/// Layout of a stored object. A header, then frames of at most <see cref="FrameSize"/> bytes of content, each
/// encrypted with AES-256-GCM:
/// <code>
/// header : magic "P4SB" (4) | version (1) | flags (1) | reserved (2) | salt (16)
/// frame  : length and last flag, big endian (4) | ciphertext (length) | tag (16)
/// </code>
/// The key of an object is derived from its salt. The nonce of a frame is its index, and its associated data are the
/// header, the index and the frame header: a frame cannot be moved, dropped or taken from another object, and an
/// object cut short lacks its last frame.
/// </summary>
internal static class ObjectFormat
{
    public const int FrameSize = 1024 * 1024;

    public const int SaltSize = 16;

    public const int HeaderSize = 8 + SaltSize;

    public const int FrameHeaderSize = 4;

    public const int TagSize = 16;

    public const int NonceSize = 12;

    public const int AssociatedDataSize = HeaderSize + 8 + FrameHeaderSize;

    public const uint LastFrameFlag = 0x8000_0000;

    public const byte Version = 1;

    /// <summary>
    /// The content was compressed with zstd before it was encrypted.
    /// </summary>
    public const byte CompressedFlag = 1;

    private static ReadOnlySpan<byte> Magic => "P4SB"u8;

    public static byte[] CreateHeader(bool compressed, ReadOnlySpan<byte> salt)
    {
        byte[] header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = Version;
        header[5] = compressed ? CompressedFlag : (byte)0;
        salt.CopyTo(header.AsSpan(8));
        return header;
    }

    /// <returns>Whether the content is compressed.</returns>
    /// <exception cref="BackupIntegrityException">The header is not the one of an object of this format.</exception>
    public static bool ParseHeader(ReadOnlySpan<byte> header, out ReadOnlySpan<byte> salt)
    {
        if (!header[..4].SequenceEqual(Magic))
        {
            throw new BackupIntegrityException("The object is not a backup object.");
        }
        if (header[4] != Version)
        {
            throw new BackupIntegrityException($"The object has version {header[4]} of the format, not {Version}.");
        }
        if ((header[5] & ~CompressedFlag) != 0 || header[6] != 0 || header[7] != 0)
        {
            throw new BackupIntegrityException("The header of the object has unknown flags.");
        }

        salt = header.Slice(8, SaltSize);
        return (header[5] & CompressedFlag) != 0;
    }

    public static void WriteNonce(Span<byte> nonce, long frameIndex)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], frameIndex);
    }

    public static void WriteAssociatedData(
        Span<byte> associatedData,
        ReadOnlySpan<byte> header,
        long frameIndex,
        uint frameHeader)
    {
        header.CopyTo(associatedData);
        BinaryPrimitives.WriteInt64BigEndian(associatedData[HeaderSize..], frameIndex);
        BinaryPrimitives.WriteUInt32BigEndian(associatedData[(HeaderSize + 8)..], frameHeader);
    }
}
