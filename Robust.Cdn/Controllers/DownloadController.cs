using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Helpers;
using Robust.Cdn.Services;
using SharpZstd.Interop;
using SQLitePCL;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("version/{version}")]
public sealed class DownloadController : ControllerBase
{
    private const int MinDownloadProtocol = 1;
    private const int MaxDownloadProtocol = 1;
    private const int MaxDownloadRequestSize = 4 * 100_000;

    private readonly Database _db;
    private readonly ILogger<DownloadController> _logger;
    private readonly DownloadRequestLogger _requestLogger;
    private readonly CdnOptions _options;

    public DownloadController(
        Database db,
        ILogger<DownloadController> logger,
        IOptionsSnapshot<CdnOptions> options,
        DownloadRequestLogger requestLogger)
    {
        _db = db;
        _logger = logger;
        _requestLogger = requestLogger;
        _options = options.Value;
    }

    [HttpGet("manifest")]
    public IActionResult GetManifest(string version)
    {
        var con = _db.Connection;
        con.BeginTransaction(deferred: true);

        var (row, hash) = con.QuerySingleOrDefault<(long, byte[])>(
            "SELECT Id, ManifestHash FROM ContentVersion WHERE Version = @Version",
            new
            {
                Version = version
            });

        if (row == 0)
            return NotFound();

        // I'll be honest I'm not sure how useful this is.
        // I just wanted to make that SELECT less lonely.
        Response.Headers["X-Manifest-Hash"] = Convert.ToHexString(hash);

        var blob = SqliteBlobStream.Open(con.Handle!, "main", "ContentVersion", "ManifestData", row, false);

        if (AcceptsZStd)
        {
            Response.Headers.ContentEncoding = "zstd";

            return File(blob, "text/plain");
        }

        var decompress = new ZStdDecompressStream(blob);

        return File(decompress, "text/plain");
    }

    [HttpOptions("download")]
    public IActionResult DownloadOptions(string version)
    {
        _ = version;

        Response.Headers["X-Robust-Download-Min-Protocol"] = MinDownloadProtocol.ToString();
        Response.Headers["X-Robust-Download-Max-Protocol"] = MaxDownloadProtocol.ToString();

        return NoContent();
    }

    [HttpPost("download")]
    public async Task<IActionResult> Download(string version)
    {
        if (Request.ContentType != "application/octet-stream")
            return BadRequest("Must specify application/octet-stream Content-Type");

        if (Request.Headers["X-Robust-Download-Protocol"] != "1")
            return BadRequest("Unknown X-Robust-Download-Protocol");

        var protocol = 1;

        // TODO: this request limiting logic is pretty bad.
        HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxDownloadRequestSize;

        var con = _db.Connection;
        con.BeginTransaction(deferred: true);

        var (versionId, countDistinctBlobs) = con.QuerySingleOrDefault<(long, int)>(
            "SELECT Id, CountDistinctBlobs FROM ContentVersion WHERE Version = @Version",
            new
            {
                Version = version
            });

        if (versionId == 0)
            return NotFound();

        var entriesCount = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM ContentManifestEntry WHERE VersionId = @VersionId",
            new
            {
                VersionId = versionId
            });

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
            var requestRatio = countFilesRequested / (float) countDistinctBlobs;
            _logger.LogTrace("Auto stream compression ratio: {RequestRatio}", requestRatio);
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
        _logger.LogTrace("Transfer is using stream-compression: {PreCompressed}", doStreamCompression);

        if (doStreamCompression)
        {
            var zStdCompressStream = new ZStdCompressStream(outStream);
            zStdCompressStream.Context.SetParameter(
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

        _logger.LogTrace("Transfer is using pre-compression: {PreCompressed}", preCompressed);

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

            SqliteBlobStream? blob = null;
            ZStdDecompressStream? decompress = null;

            try
            {
                using var stmt =
                    con.Handle!.Prepare(
                        "SELECT c.Compression, c.Size, c.Id " +
                        "FROM ContentManifestEntry cme " +
                        "INNER JOIN Content c on c.Id = cme.ContentId " +
                        "WHERE cme.VersionId = @VersionId AND cme.ManifestIdx = @ManifestIdx");

                stmt.BindInt64(1, versionId); // @VersionId

                offset = 0;
                var swSqlite = new Stopwatch();
                var count = 0;
                while (offset < buf.Length)
                {
                    var index = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset, 4).Span);

                    swSqlite.Start();
                    stmt.BindInt(2, index);

                    if (stmt.Step() != raw.SQLITE_ROW)
                        throw new InvalidOperationException("Unable to find manifest row??");

                    var compression = (ContentCompression)stmt.ColumnInt(0);
                    var size = stmt.ColumnInt(1);
                    var rowId = stmt.ColumnInt64(2);

                    stmt.Reset();
                    swSqlite.Stop();

                    // _aczSawmill.Debug($"{index:D5}: {blobLength:D8} {dataOffset:D8} {dataLength:D8}");

                    BinaryPrimitives.WriteInt32LittleEndian(fileHeader, size);

                    if (blob == null)
                    {
                        blob = SqliteBlobStream.Open(con.Handle!, "main", "Content", "Data", rowId, false);
                        if (!preCompressed)
                            decompress = new ZStdDecompressStream(blob, ownStream: false);
                    }
                    else
                    {
                        blob.Reopen(rowId);
                    }

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

                _logger.LogTrace(
                    "Total SQLite: {SqliteElapsed} ms, ns / iter: {NanosPerIter}",
                    swSqlite.ElapsedMilliseconds,
                    swSqlite.Elapsed.TotalMilliseconds * 1_000_000 / count);
            }
            finally
            {
                blob?.Dispose();
                decompress?.Dispose();
            }
        }

        var bytesSent = countStream.Written;
        _logger.LogTrace("Total data sent: {BytesSent} B", bytesSent);

        if (_options.LogRequests)
        {
            var logCompression = DownloadRequestLogger.RequestLogCompression.None;
            if (preCompressed)
                logCompression |= DownloadRequestLogger.RequestLogCompression.PreCompress;
            if (doStreamCompression)
                logCompression |= DownloadRequestLogger.RequestLogCompression.Stream;

            var log = new DownloadRequestLogger.RequestLog(
                buf, logCompression, protocol, DateTime.UtcNow, versionId, bytesSent);

            await _requestLogger.QueueLog(log);
        }

        return new NoOpActionResult();
    }

    // TODO: Crappy Accept-Encoding parser
    private bool AcceptsZStd => Request.Headers.AcceptEncoding.Count > 0
                                && Request.Headers.AcceptEncoding[0].Contains("zstd");

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
