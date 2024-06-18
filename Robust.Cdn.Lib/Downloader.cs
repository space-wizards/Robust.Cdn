using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using SharpZstd;

namespace Robust.Cdn.Lib;

public static class Downloader
{
    // ReSharper disable once ConvertToConstant.Global
    public static readonly int ManifestDownloadProtocolVersion = 1;

    public static async Task<DownloadReader> DownloadFilesAsync(
        HttpClient client,
        string downloadUrl,
        IEnumerable<int> downloadIndices,
        CancellationToken cancel = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, downloadUrl);
        request.Content = new ByteArrayContent(BuildRequestBody(downloadIndices, out var totalFiles));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Octet);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));
        request.Headers.Add(
            "X-Robust-Download-Protocol",
            ManifestDownloadProtocolVersion.ToString(CultureInfo.InvariantCulture));

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        try
        {
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(cancel);
            if (response.Content.Headers.ContentEncoding.Contains("zstd"))
                stream = new ZstdDecodeStream(stream, leaveOpen: false);

            try
            {
                var header = await ReadStreamHeaderAsync(stream, cancel);

                return new DownloadReader(response, stream, header, totalFiles);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static byte[] BuildRequestBody(IEnumerable<int> indices, out int totalFiles)
    {
        var toDownload = indices.ToArray();
        var requestBody = new byte[toDownload.Length * 4];
        var reqI = 0;
        foreach (var idx in toDownload)
        {
            BinaryPrimitives.WriteInt32LittleEndian(requestBody.AsSpan(reqI, 4), idx);
            reqI += 4;
        }

        totalFiles = toDownload.Length;
        return requestBody;
    }

    private static async Task<DownloadStreamHeaderData> ReadStreamHeaderAsync(Stream stream, CancellationToken cancel)
    {
        var streamHeader = await stream.ReadExactAsync(4, cancel);
        var streamFlags = (DownloadStreamHeaderFlags)BinaryPrimitives.ReadInt32LittleEndian(streamHeader);

        return new DownloadStreamHeaderData
        {
            Flags = streamFlags
        };
    }
}

[Flags]
public enum DownloadStreamHeaderFlags
{
    None = 0,

    /// <summary>
    /// If this flag is set on the download stream, individual files have been pre-compressed by the server.
    /// This means each file has a compression header, and the launcher should not attempt to compress files itself.
    /// </summary>
    PreCompressed = 1 << 0
}

public sealed class DownloadStreamHeaderData
{
    public DownloadStreamHeaderFlags Flags { get; init; }

    public bool PreCompressed => (Flags & DownloadStreamHeaderFlags.PreCompressed) != 0;
}

public sealed class DownloadReader : IDisposable
{
    private readonly Stream _stream;
    private readonly HttpResponseMessage _httpResponse;
    private readonly int _totalFileCount;
    private readonly byte[] _headerReadBuffer;
    public DownloadStreamHeaderData Data { get; }

    private int _filesRead;
    private State _state = State.ReadFileHeader;
    private FileHeaderData _currentHeader;

    internal DownloadReader(
        HttpResponseMessage httpResponse,
        Stream stream,
        DownloadStreamHeaderData data,
        int totalFileCount)
    {
        _stream = stream;
        Data = data;
        _totalFileCount = totalFileCount;
        _httpResponse = httpResponse;
        _headerReadBuffer = new byte[data.PreCompressed ? 8 : 4];
    }

    public async ValueTask<FileHeaderData?> ReadFileHeaderAsync(CancellationToken cancel = default)
    {
        CheckState(State.ReadFileHeader);

        if (_filesRead >= _totalFileCount)
            return null;

        await _stream.ReadExactlyAsync(_headerReadBuffer, cancel);

        var length = BinaryPrimitives.ReadInt32LittleEndian(_headerReadBuffer.AsSpan(0, 4));
        var compressedLength = 0;

        if (Data.PreCompressed)
            compressedLength = BinaryPrimitives.ReadInt32LittleEndian(_headerReadBuffer.AsSpan(4, 4));

        _currentHeader = new FileHeaderData
        {
            DataLength = length,
            CompressedLength = compressedLength
        };

        _state = State.ReadFileContents;
        _filesRead += 1;

        return _currentHeader;
    }

    public async ValueTask ReadRawFileContentsAsync(Memory<byte> buffer, CancellationToken cancel = default)
    {
        CheckState(State.ReadFileContents);

        var size = _currentHeader.IsPreCompressed ? _currentHeader.CompressedLength : _currentHeader.DataLength;
        if (size > buffer.Length)
            throw new ArgumentException("Provided buffer is not large enough to fit entire data size");

        await _stream.ReadExactlyAsync(buffer, cancel);

        _state = State.ReadFileHeader;
    }

    public async ValueTask ReadFileContentsAsync(Stream destination, CancellationToken cancel = default)
    {
        CheckState(State.ReadFileContents);

        if (_currentHeader.IsPreCompressed)
        {
            // TODO: Buffering can be avoided here.
            var compressedBuffer = ArrayPool<byte>.Shared.Rent(_currentHeader.CompressedLength);

            await _stream.ReadExactlyAsync(compressedBuffer, cancel);

            var ms = new MemoryStream(compressedBuffer, writable: false);
            await using var decompress = new ZstdDecodeStream(ms, false);

            await decompress.CopyToAsync(destination, cancel);

            ArrayPool<byte>.Shared.Return(compressedBuffer);
        }
        else
        {
            await _stream.CopyAmountToAsync(destination, _currentHeader.DataLength, 4096, cancel);
        }

        _state = State.ReadFileHeader;
    }

    private void CheckState(State expectedState)
    {
        if (expectedState != _state)
            throw new InvalidOperationException($"Invalid state! Expected {expectedState}, but was {_state}");
    }

    public enum State : byte
    {
        ReadFileHeader,
        ReadFileContents
    }

    public struct FileHeaderData
    {
        public int DataLength;
        public int CompressedLength;

        public bool IsPreCompressed => CompressedLength > 0;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _httpResponse.Dispose();
    }
}
