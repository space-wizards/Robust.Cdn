namespace Robust.Cdn.Helpers;

public sealed class CountWriteStream : Stream
{
    private readonly Stream _baseStream;
    public long Written { get; private set; }

    public CountWriteStream(Stream baseStream)
    {
        if (!baseStream.CanWrite)
            throw new ArgumentException("Stream must be writeable", nameof(baseStream));

        _baseStream = baseStream;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _baseStream.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        return _baseStream.DisposeAsync();
    }

    public override void Flush()
    {
        _baseStream.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _baseStream.FlushAsync(cancellationToken);
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

    public override void Write(byte[] buffer, int offset, int count)
    {
        Written += count;
        _baseStream.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = new CancellationToken())
    {
        Written += buffer.Length;
        return _baseStream.WriteAsync(buffer, cancellationToken);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
}
