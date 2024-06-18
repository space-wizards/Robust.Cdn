using System.Buffers;

namespace Robust.Cdn.Lib;

internal static class StreamHelper
{
    public static async ValueTask<byte[]> ReadExactAsync(this Stream stream, int amount, CancellationToken cancel)
    {
        var data = new byte[amount];
        await stream.ReadExactlyAsync(data, cancel);
        return data;
    }

    public static async Task CopyAmountToAsync(
        this Stream stream,
        Stream to,
        int amount,
        int bufferSize,
        CancellationToken cancel)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        while (amount > 0)
        {
            Memory<byte> readInto = buffer;
            if (amount < readInto.Length)
                readInto = readInto[..amount];

            var read = await stream.ReadAsync(readInto, cancel);
            if (read == 0)
                throw new EndOfStreamException();

            amount -= read;

            readInto = readInto[..read];

            await to.WriteAsync(readInto, cancel);
        }

        ArrayPool<byte>.Shared.Return(buffer);
    }
}
