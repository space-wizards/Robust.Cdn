namespace Robust.Cdn.Helpers;

public static class StreamHelpers
{
    public static void ReadExact(this Stream stream, Span<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var cRead = stream.Read(buffer);
            if (cRead == 0)
                throw new EndOfStreamException();

            buffer = buffer[cRead..];
        }
    }

}
