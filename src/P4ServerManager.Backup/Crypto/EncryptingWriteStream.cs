using System.Buffers.Binary;
using System.Security.Cryptography;

namespace P4ServerManager.Backup.Crypto;

/// <summary>
/// Encrypts what is written to it into the <see cref="ObjectFormat"/>, frame by frame. <see cref="FinishAsync"/> writes
/// the last frame: an object whose stream is disposed without it is cut short, and reading it fails.
/// </summary>
internal sealed class EncryptingWriteStream : Stream
{
    private readonly Stream inner;
    private readonly AesGcm aes;
    private readonly byte[] header;
    private readonly byte[] plaintext = new byte[ObjectFormat.FrameSize];
    private readonly byte[] frame =
        new byte[ObjectFormat.FrameHeaderSize + ObjectFormat.FrameSize + ObjectFormat.TagSize];

    private int buffered;
    private long frameIndex;
    private bool headerWritten;
    private bool finished;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !finished;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    
    /// <param name="inner">Where the object goes. It is left open.</param>
    /// <param name="keys">Keys of the repository.</param>
    /// <param name="compressed">Whether what is written is compressed, as the header tells.</param>
    public EncryptingWriteStream(Stream inner, RepositoryKeys keys, bool compressed)
    {
        this.inner = inner;
        byte[] salt = RandomNumberGenerator.GetBytes(ObjectFormat.SaltSize);
        header = ObjectFormat.CreateHeader(compressed, salt);
        byte[] key = keys.DeriveObjectKey(salt);
        aes = new AesGcm(key, ObjectFormat.TagSize);
        CryptographicOperations.ZeroMemory(key);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(finished, this);
        if (!headerWritten)
        {
            inner.Write(header);
            headerWritten = true;
        }

        while (!buffer.IsEmpty)
        {
            // A full frame is sealed only once more content comes: the last one must carry the flag.
            if (buffered == plaintext.Length)
            {
                inner.Write(frame, 0, SealFrame(last: false));
            }

            int count = Math.Min(buffer.Length, plaintext.Length - buffered);
            buffer[..count].CopyTo(plaintext.AsSpan(buffered));
            buffered += count;
            buffer = buffer[count..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(finished, this);
        if (!headerWritten)
        {
            await inner.WriteAsync(header, cancellationToken);
            headerWritten = true;
        }

        while (!buffer.IsEmpty)
        {
            if (buffered == plaintext.Length)
            {
                await inner.WriteAsync(frame.AsMemory(0, SealFrame(last: false)), cancellationToken);
            }

            int count = Math.Min(buffer.Length, plaintext.Length - buffered);
            buffer.Span[..count].CopyTo(plaintext.AsSpan(buffered));
            buffered += count;
            buffer = buffer[count..];
        }
    }

    /// <summary>
    /// Writes the last frame. Nothing can be written after it.
    /// </summary>
    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(finished, this);
        if (!headerWritten)
        {
            await inner.WriteAsync(header, cancellationToken);
            headerWritten = true;
        }

        await inner.WriteAsync(frame.AsMemory(0, SealFrame(last: true)), cancellationToken);
        finished = true;
        await inner.FlushAsync(cancellationToken);
    }

    public override void Flush()
    {
        // Frames are written whole: what is buffered waits for the next frame.
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            aes.Dispose();
            CryptographicOperations.ZeroMemory(plaintext);
            finished = true;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Encrypts what is buffered into <see cref="frame"/>.
    /// </summary>
    /// <returns>The length of the frame.</returns>
    private int SealFrame(bool last)
    {
        uint frameHeader = (uint)buffered | (last ? ObjectFormat.LastFrameFlag : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(frame, frameHeader);

        Span<byte> nonce = stackalloc byte[ObjectFormat.NonceSize];
        ObjectFormat.WriteNonce(nonce, frameIndex);
        Span<byte> associatedData = stackalloc byte[ObjectFormat.AssociatedDataSize];
        ObjectFormat.WriteAssociatedData(associatedData, header, frameIndex, frameHeader);

        aes.Encrypt(
            nonce,
            plaintext.AsSpan(0, buffered),
            frame.AsSpan(ObjectFormat.FrameHeaderSize, buffered),
            frame.AsSpan(ObjectFormat.FrameHeaderSize + buffered, ObjectFormat.TagSize),
            associatedData);

        int length = ObjectFormat.FrameHeaderSize + buffered + ObjectFormat.TagSize;
        frameIndex++;
        buffered = 0;
        return length;
    }
}
