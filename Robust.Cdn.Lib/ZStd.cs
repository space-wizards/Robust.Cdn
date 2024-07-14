using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpZstd.Interop;
using static SharpZstd.Interop.Zstd;

[assembly: InternalsVisibleTo("Robust.Cdn")]

namespace Robust.Cdn.Lib;

internal static class ZStd
{
    public static int CompressBound(int length)
    {
        return (int)ZSTD_COMPRESSBOUND((nuint)length);
    }
}

[Serializable]
internal sealed class ZStdException : Exception
{
    public ZStdException()
    {
    }

    public ZStdException(string message) : base(message)
    {
    }

    public ZStdException(string message, Exception inner) : base(message, inner)
    {
    }

    public static unsafe ZStdException FromCode(nuint code)
    {
        return new ZStdException(Marshal.PtrToStringUTF8((IntPtr)ZSTD_getErrorName(code))!);
    }

    public static void ThrowIfError(nuint code)
    {
        if (ZSTD_isError(code) != 0)
            throw FromCode(code);
    }
}

public sealed unsafe class ZStdCompressionContext : IDisposable
{
    public ZSTD_CCtx* Context { get; private set; }

    private bool Disposed => Context == null;

    public ZStdCompressionContext()
    {
        Context = ZSTD_createCCtx();
    }

    public void SetParameter(ZSTD_cParameter parameter, int value)
    {
        CheckDisposed();

        ZSTD_CCtx_setParameter(Context, parameter, value);
    }

    public int Compress(Span<byte> destination, Span<byte> source, int compressionLevel = ZSTD_CLEVEL_DEFAULT)
    {
        CheckDisposed();

        fixed (byte* dst = destination)
        fixed (byte* src = source)
        {
            var ret = ZSTD_compressCCtx(
                Context,
                dst, (nuint)destination.Length,
                src, (nuint)source.Length,
                compressionLevel);

            ZStdException.ThrowIfError(ret);
            return (int)ret;
        }
    }

    ~ZStdCompressionContext()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (Disposed)
            return;

        ZSTD_freeCCtx(Context);
        Context = null;
        GC.SuppressFinalize(this);
    }

    private void CheckDisposed()
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(ZStdCompressionContext));
    }
}

internal sealed class ZStdDecompressStream : Stream
{
    private readonly Stream _baseStream;
    private readonly bool _ownStream;
    private readonly unsafe ZSTD_DCtx* _ctx;
    private readonly byte[] _buffer;
    private int _bufferPos;
    private int _bufferSize;
    private bool _disposed;

    public unsafe ZStdDecompressStream(Stream baseStream, bool ownStream = true)
    {
        _baseStream = baseStream;
        _ownStream = ownStream;
        _ctx = ZSTD_createDCtx();
        _buffer = ArrayPool<byte>.Shared.Rent((int)ZSTD_DStreamInSize());
    }

    protected override unsafe void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;
        ZSTD_freeDCtx(_ctx);

        if (disposing)
        {
            if (_ownStream)
                _baseStream.Dispose();

            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        _baseStream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int ReadByte()
    {
        Span<byte> buf = stackalloc byte[1];
        return Read(buf) == 0 ? -1 : buf[0];
    }

    public override unsafe int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        do
        {
            if (_bufferSize == 0 || _bufferPos == _bufferSize)
            {
                _bufferPos = 0;
                _bufferSize = _baseStream.Read(_buffer);

                if (_bufferSize == 0)
                    return 0;
            }

            fixed (byte* inputPtr = _buffer)
            fixed (byte* outputPtr = buffer)
            {
                var outputBuf = new ZSTD_outBuffer { dst = outputPtr, pos = 0, size = (nuint)buffer.Length };
                var inputBuf = new ZSTD_inBuffer { src = inputPtr, pos = (nuint)_bufferPos, size = (nuint)_bufferSize };
                var ret = ZSTD_decompressStream(_ctx, &outputBuf, &inputBuf);

                _bufferPos = (int)inputBuf.pos;
                ZStdException.ThrowIfError(ret);

                if (outputBuf.pos > 0)
                    return (int)outputBuf.pos;
            }
        } while (true);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        do
        {
            if (_bufferSize == 0 || _bufferPos == _bufferSize)
            {
                _bufferPos = 0;
                _bufferSize = await _baseStream.ReadAsync(_buffer, cancellationToken);

                if (_bufferSize == 0)
                    return 0;
            }

            var outputPos = DecompressChunk();
            if (outputPos > 0)
                return outputPos;
        } while (true);

        unsafe int DecompressChunk()
        {
            fixed (byte* inputPtr = _buffer)
            fixed (byte* outputPtr = buffer.Span)
            {
                ZSTD_outBuffer outputBuf = default;
                outputBuf.dst = outputPtr;
                outputBuf.pos = 0;
                outputBuf.size = (nuint)buffer.Length;
                ZSTD_inBuffer inputBuf = default;
                inputBuf.src = inputPtr;
                inputBuf.pos = (nuint)_bufferPos;
                inputBuf.size = (nuint)_bufferSize;

                var ret = ZSTD_decompressStream(_ctx, &outputBuf, &inputBuf);

                _bufferPos = (int)inputBuf.pos;
                ZStdException.ThrowIfError(ret);

                return (int)outputBuf.pos;
            }
        }
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

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ZStdDecompressStream));
    }
}
