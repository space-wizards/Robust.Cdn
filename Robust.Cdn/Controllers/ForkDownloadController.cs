using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.DataAccessLayer;
using Robust.Cdn.Helpers;
using Robust.Cdn.Lib;
using Robust.Cdn.Services;
using SharpZstd;
using SharpZstd.Interop;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/fork/{fork}/version/{version}")]
public sealed class DownloadController(
    Database db,
    ILogger<DownloadController> logger,
    IOptionsSnapshot<CdnOptions> options,
    DownloadRequestLogger requestLogger)
    : ControllerBase
{
    private const int MinDownloadProtocol = 1;
    private const int MaxDownloadProtocol = 1;
    private const int MaxDownloadRequestSize = 4 * 100_000;

    private readonly CdnOptions _options = options.Value;

    [HttpGet("manifest")]
    public IActionResult GetManifest(string fork, string version)
    {
        db.StartTransaction(true);
        var manifestBlob = db.FindManifestDataBlob(fork, version);
        if (manifestBlob == null)
            return NotFound();

        // I'll be honest I'm not sure how useful this is.
        // I just wanted to make that SELECT less lonely.
        Response.Headers["X-Manifest-Hash"] = manifestBlob.ManifestHash;


        if (AcceptsZStd)
        {
            Response.Headers.ContentEncoding = "zstd";

            return File(manifestBlob.Blob, "text/plain; charset=utf-8");
        }

        var decompress = new ZstdDecodeStream(manifestBlob.Blob, leaveOpen: false);

        return File(decompress, "text/plain; charset=utf-8");
    }

    [HttpOptions("download")]
    public IActionResult DownloadOptions(string fork, string version)
    {
        _ = fork;
        _ = version;

        Response.Headers["X-Robust-Download-Min-Protocol"] = MinDownloadProtocol.ToString();
        Response.Headers["X-Robust-Download-Max-Protocol"] = MaxDownloadProtocol.ToString();

        return NoContent();
    }

    [HttpPost("download")]
    public async Task<IActionResult> Download(string fork, string version)
    {
        if (Request.ContentType != "application/octet-stream")
            return BadRequest("Must specify application/octet-stream Content-Type");

        if (Request.Headers["X-Robust-Download-Protocol"] != "1")
            return BadRequest("Unknown X-Robust-Download-Protocol");

        var protocol = 1;

        // TODO: this request limiting logic is pretty bad.
        HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxDownloadRequestSize;

        db.StartTransaction(deferred: true);

        var result = db.GetDistinctBlobsAndManifestEntriesCounts(fork, version);
        if (result == null)
            return NotFound();
        var (versionId, countDistinctBlobs, entriesCount) = result.Value;

        var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer);

        var buf = buffer.GetBuffer().AsMemory(0, (int)buffer.Position);

        var bits = new BitArray(entriesCount);
        var offset = 0;
        var countFilesRequested = 0;
        while (offset < buf.Length)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset, 4).Span);

            if (index < 0 || index >= entriesCount)
                return BadRequest("Out of bounds manifest index");

            if (bits[index])
                return BadRequest("Cannot request file twice");

            bits[index] = true;

            offset += 4;
            countFilesRequested += 1;
        }

        var outStream = Response.Body;

        var countStream = new CountWriteStream(outStream);
        outStream = countStream;

        var optStreamCompression = _options.StreamCompress;
        var optPreCompression = _options.SendPreCompressed;
        var optAutoStreamCompressRatio = _options.AutoStreamCompressRatio;

        if (optAutoStreamCompressRatio > 0)
        {
            var requestRatio = countFilesRequested / (float)countDistinctBlobs;
            logger.LogTrace("Auto stream compression ratio: {RequestRatio}", requestRatio);
            if (requestRatio > optAutoStreamCompressRatio)
            {
                optStreamCompression = true;
                optPreCompression = false;
            }
            else
            {
                optStreamCompression = false;
                optPreCompression = true;
            }
        }

        var doStreamCompression = optStreamCompression && AcceptsZStd;
        logger.LogTrace("Transfer is using stream-compression: {PreCompressed}", doStreamCompression);

        if (doStreamCompression)
        {
            var zStdCompressStream = new ZstdEncodeStream(outStream, leaveOpen: false);
            zStdCompressStream.Encoder.SetParameter(
                ZSTD_cParameter.ZSTD_c_compressionLevel,
                _options.StreamCompressLevel);

            outStream = zStdCompressStream;
            Response.Headers.ContentEncoding = "zstd";
        }

        // Compression options for individual compression get kind of gnarly here:
        // We cannot assume that the database was constructed with the current set of options
        // that is, individual compression and such.
        // If you ingest all versions with individual compression OFF then enable it,
        // we have no way to know whether the current blobs are properly compressed.
        // Also, you can have individual compression OFF now, and still have compressed blobs in the DB.
        // For this reason, we basically ignore CdnOptions.IndividualCompression here, unlike engine-side ACZ.
        // Whether pre-compression is done is actually based off IndividualDecompression instead.
        // Stream compression does not do overriding behavior it just sits on top of everything if you turn it on.

        var preCompressed = optPreCompression;

        logger.LogTrace("Transfer is using pre-compression: {PreCompressed}", preCompressed);

        var fileHeaderSize = 4;
        if (preCompressed)
            fileHeaderSize += 4;

        var fileHeader = new byte[fileHeaderSize];

        await using (outStream)
        {
            var streamHeader = new byte[4];
            DownloadStreamHeaderFlags streamHeaderFlags = 0;
            if (preCompressed)
                streamHeaderFlags |= DownloadStreamHeaderFlags.PreCompressed;

            BinaryPrimitives.WriteInt32LittleEndian(streamHeader, (int)streamHeaderFlags);

            await outStream.WriteAsync(streamHeader);

            ZStdDecompressStream? decompress = null;

            try
            {
                offset = 0;
                var swSqlite = new Stopwatch();
                var count = 0;
                while (offset < buf.Length)
                {
                    var index = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset, 4).Span);

                    swSqlite.Start();

                    var (compression, size, rowId) = db.ListContentMetadata(versionId, index);

                    swSqlite.Stop();

                    BinaryPrimitives.WriteInt32LittleEndian(fileHeader, size);

                    var blob = db.OpenContentBlobForRead(rowId);
                    if (!preCompressed)
                        decompress = new ZStdDecompressStream(blob, ownStream: false);

                    Stream copyFromStream = blob;
                    if (preCompressed)
                    {
                        // If we are doing pre-compression, just write the DB contents directly.
                        BinaryPrimitives.WriteInt32LittleEndian(
                            fileHeader.AsSpan(4, 4),
                            compression == ContentCompression.ZStd ? (int)blob.Length : 0);
                    }
                    else if (compression == ContentCompression.ZStd)
                    {
                        // If we are not doing pre-compression but the DB entry is compressed, we have to decompress!
                        copyFromStream = decompress!;
                    }

                    await outStream.WriteAsync(fileHeader);

                    await copyFromStream.CopyToAsync(outStream);

                    offset += 4;
                    count += 1;
                }

                logger.LogTrace(
                    "Total SQLite: {SqliteElapsed} ms, ns / iter: {NanosPerIter}",
                    swSqlite.ElapsedMilliseconds,
                    swSqlite.Elapsed.TotalMilliseconds * 1_000_000 / count);
            }
            finally
            {
                decompress?.Dispose();
            }
        }

        var bytesSent = countStream.Written;
        logger.LogTrace("Total data sent: {BytesSent} B", bytesSent);

        if (_options.LogRequests)
        {
            var logCompression = DownloadRequestLogger.RequestLogCompression.None;
            if (preCompressed)
                logCompression |= DownloadRequestLogger.RequestLogCompression.PreCompress;
            if (doStreamCompression)
                logCompression |= DownloadRequestLogger.RequestLogCompression.Stream;

            var log = new DownloadRequestLogger.RequestLog(
                buf, logCompression, protocol, DateTime.UtcNow, versionId, bytesSent);

            await requestLogger.QueueLog(log);
        }

        return new NoOpActionResult();
    }

    // TODO: Crappy Accept-Encoding parser
    private bool AcceptsZStd => Request.Headers.AcceptEncoding.Count > 0
                                && Request.Headers.AcceptEncoding[0] is { } header
                                && header.Contains("zstd");

    public sealed class NoOpActionResult : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            return Task.CompletedTask;
        }
    }

    [Flags]
    private enum DownloadStreamHeaderFlags
    {
        None = 0,

        /// <summary>
        /// If this flag is set on the download stream, individual files have been pre-compressed by the server.
        /// This means each file has a compression header, and the launcher should not attempt to compress files itself.
        /// </summary>
        PreCompressed = 1 << 0
    }
}
