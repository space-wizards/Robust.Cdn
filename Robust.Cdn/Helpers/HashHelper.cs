using System.Buffers;
using System.Runtime.InteropServices;
using SpaceWizards.Sodium;

namespace Robust.Cdn.Helpers;

internal static class HashHelper
{
    public static unsafe byte[] HashBlake2B(Stream stream)
    {
        var statePointer = NativeMemory.AlignedAlloc((UIntPtr)sizeof(CryptoGenericHashBlake2B.State), 64);
        ref var state = ref *(CryptoGenericHashBlake2B.State*)statePointer;

        var buffer = ArrayPool<byte>.Shared.Rent(4096);

        var result = new byte[CryptoGenericHashBlake2B.Bytes];

        try
        {
            CryptoGenericHashBlake2B.Init(ref state, ReadOnlySpan<byte>.Empty, result.Length);

            while (true)
            {
                var readAmount = stream.Read(buffer);
                if (readAmount == 0)
                    break;

                var readData = buffer.AsSpan(0, readAmount);
                CryptoGenericHashBlake2B.Update(ref state, readData);
            }

            CryptoGenericHashBlake2B.Final(ref state, result);
            return result;
        }
        finally
        {
            NativeMemory.AlignedFree(statePointer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
