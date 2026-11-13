namespace P4ServerManager.Backup.Targets;

/// <summary>
/// A window of a seekable stream, such as one part of a multipart upload, read without copying it. It is seekable
/// itself, so that an SDK can retry the part.
/// </summary>
public sealed class ReadOnlySubStream : Stream
{
    private readonly Stream inner;
    private readonly long start;
    private readonly long length;
    private long position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => position;
        set => position = Math.Clamp(value, 0, length);
    }
    
    /// <param name="inner">The stream. It is left open, and is moved by every read.</param>
    /// <param name="start">Where the window starts.</param>
    /// <param name="length">Length of the window.</param>
    public ReadOnlySubStream(Stream inner, long start, long length)
    {
        if (!inner.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(inner));
        }
        this.inner = inner;
        this.start = start;
        this.length = length;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        int count = (int)Math.Min(buffer.Length, length - position);
        if (count <= 0)
        {
            return 0;
        }
        inner.Position = start + position;
        int read = inner.Read(buffer[..count]);
        position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int count = (int)Math.Min(buffer.Length, length - position);
        if (count <= 0)
        {
            return 0;
        }
        inner.Position = start + position;
        int read = await inner.ReadAsync(buffer[..count], cancellationToken);
        position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
