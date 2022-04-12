using System.Buffers;

namespace Robust.Cdn.Helpers;

/// <summary>
/// Helpers for dealing with buffer-like arrays.
/// </summary>
public static class BufferHelpers
{
    /// <summary>
    /// Resize the given buffer to the next power of two that fits the needed size.
    /// Takes an array pool to rent/return with.
    /// The contents of the buffer are NOT preserved across resizes.
    /// </summary>
    public static void EnsurePooledBuffer<T>(ref T[] buf, ArrayPool<T> pool, int minimumLength)
    {
        if (buf.Length >= minimumLength)
            return;

        pool.Return(buf);
        buf = pool.Rent(minimumLength);
    }
}
