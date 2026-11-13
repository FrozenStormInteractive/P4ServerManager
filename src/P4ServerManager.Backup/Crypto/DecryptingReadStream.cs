using System.Buffers.Binary;
using System.Security.Cryptography;

namespace P4ServerManager.Backup.Crypto;

/// <summary>
/// Reads the content of an object in the <see cref="ObjectFormat"/>, frame by frame, and checks each one. A frame
/// that was changed, an object cut short or bytes after its last frame fail with a
/// <see cref="BackupIntegrityException"/>.
/// </summary>
internal sealed class DecryptingReadStream : Stream
{
    private readonly Stream inner;
    private readonly AesGcm aes;
    private readonly byte[] header;
    private readonly byte[] frame = new byte[ObjectFormat.FrameSize + ObjectFormat.TagSize];
    private readonly byte[] plaintext = new byte[ObjectFormat.FrameSize];

    private int offset;
    private int length;
    private long frameIndex;
    private bool lastFrameRead;

    /// <summary>
    /// Whether the content was compressed before it was encrypted: what this stream reads is then still compressed.
    /// </summary>
    public bool Compressed { get; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    
    /// <summary>
    /// Derives the key of the object from the salt in its header. <see cref="OpenAsync"/> reads that header first.
    /// </summary>
    /// <param name="inner">The stored object, positioned right after its header. It is disposed with the stream.</param>
    /// <param name="keys">Keys of the repository, from which the key of the object is derived.</param>
    /// <param name="header">The header of the object, already read from <paramref name="inner"/>: it gives the salt and
    /// the compression flag, and is part of the associated data of every frame.</param>
    private DecryptingReadStream(Stream inner, RepositoryKeys keys, byte[] header)
    {
        this.inner = inner;
        this.header = header;
        Compressed = ObjectFormat.ParseHeader(header, out ReadOnlySpan<byte> salt);
        byte[] key = keys.DeriveObjectKey(salt);
        aes = new AesGcm(key, ObjectFormat.TagSize);
        CryptographicOperations.ZeroMemory(key);
    }

    /// <summary>
    /// Reads the header of the object.
    /// </summary>
    /// <param name="inner">The stored object. It is disposed with the stream.</param>
    /// <param name="keys">Keys of the repository.</param>
    /// <param name="cancellationToken">Aborts the read.</param>
    public static async Task<DecryptingReadStream> OpenAsync(
        Stream inner,
        RepositoryKeys keys,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[ObjectFormat.HeaderSize];
        try
        {
            await inner.ReadExactlyAsync(header, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            await inner.DisposeAsync();
            throw new BackupIntegrityException("The object is cut short: it has no complete header.", exception);
        }

        try
        {
            return new DecryptingReadStream(inner, keys, header);
        }
        catch
        {
            await inner.DisposeAsync();
            throw;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        while (offset == length)
        {
            if (lastFrameRead)
            {
                return 0;
            }
            ReadFrame();
        }
        return CopyTo(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (offset == length)
        {
            if (lastFrameRead)
            {
                return 0;
            }
            await ReadFrameAsync(cancellationToken);
        }
        return CopyTo(buffer.Span);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            aes.Dispose();
            CryptographicOperations.ZeroMemory(plaintext);
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        aes.Dispose();
        CryptographicOperations.ZeroMemory(plaintext);
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private int CopyTo(Span<byte> buffer)
    {
        int count = Math.Min(buffer.Length, length - offset);
        plaintext.AsSpan(offset, count).CopyTo(buffer);
        offset += count;
        return count;
    }

    private void ReadFrame()
    {
        Span<byte> frameHeaderBytes = stackalloc byte[ObjectFormat.FrameHeaderSize];
        ReadInnerExactly(frameHeaderBytes);
        uint frameHeader = BinaryPrimitives.ReadUInt32BigEndian(frameHeaderBytes);
        int frameLength = GetFrameLength(frameHeader);
        ReadInnerExactly(frame.AsSpan(0, frameLength + ObjectFormat.TagSize));
        OpenFrame(frameHeader, frameLength);
        if (lastFrameRead && inner.Read(stackalloc byte[1]) != 0)
        {
            throw new BackupIntegrityException("The object has data after its last frame.");
        }
    }

    private async Task ReadFrameAsync(CancellationToken cancellationToken)
    {
        byte[] frameHeaderBytes = new byte[ObjectFormat.FrameHeaderSize];
        await ReadInnerExactlyAsync(frameHeaderBytes, cancellationToken);
        uint frameHeader = BinaryPrimitives.ReadUInt32BigEndian(frameHeaderBytes);
        int frameLength = GetFrameLength(frameHeader);
        await ReadInnerExactlyAsync(frame.AsMemory(0, frameLength + ObjectFormat.TagSize), cancellationToken);
        OpenFrame(frameHeader, frameLength);
        if (lastFrameRead && await inner.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            throw new BackupIntegrityException("The object has data after its last frame.");
        }
    }

    private static int GetFrameLength(uint frameHeader)
    {
        int frameLength = (int)(frameHeader & ~ObjectFormat.LastFrameFlag);
        if (frameLength > ObjectFormat.FrameSize)
        {
            throw new BackupIntegrityException("A frame of the object is longer than the format allows.");
        }
        return frameLength;
    }

    private void OpenFrame(uint frameHeader, int frameLength)
    {
        Span<byte> nonce = stackalloc byte[ObjectFormat.NonceSize];
        ObjectFormat.WriteNonce(nonce, frameIndex);
        Span<byte> associatedData = stackalloc byte[ObjectFormat.AssociatedDataSize];
        ObjectFormat.WriteAssociatedData(associatedData, header, frameIndex, frameHeader);
        try
        {
            aes.Decrypt(
                nonce,
                frame.AsSpan(0, frameLength),
                frame.AsSpan(frameLength, ObjectFormat.TagSize),
                plaintext.AsSpan(0, frameLength),
                associatedData);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new BackupIntegrityException(
                $"Frame {frameIndex} of the object is corrupted, or was encrypted with another key.",
                exception);
        }

        offset = 0;
        length = frameLength;
        frameIndex++;
        lastFrameRead = (frameHeader & ObjectFormat.LastFrameFlag) != 0;
    }

    private void ReadInnerExactly(Span<byte> buffer)
    {
        try
        {
            inner.ReadExactly(buffer);
        }
        catch (EndOfStreamException exception)
        {
            throw new BackupIntegrityException("The object is cut short.", exception);
        }
    }

    private async Task ReadInnerExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await inner.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new BackupIntegrityException("The object is cut short.", exception);
        }
    }
}
