using System.Buffers.Binary;
using System.Collections;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Helpers;
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
    private readonly IOptions<CdnOptions> _options;

    public DownloadController(
        Database db,
        ILogger<DownloadController> logger,
        IOptions<CdnOptions> options)
    {
        _db = db;
        _logger = logger;
        _options = options;
    }

    [HttpGet("manifest")]
    public IActionResult GetManifest(string version)
    {
        var con = _db.Connection;
        con.BeginTransaction();

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

        // TODO: this request limiting logic is pretty bad.
        HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxDownloadRequestSize;

        var options = _options.Value;
        var con = _db.Connection;
        con.BeginTransaction();

        var versionId = con.QuerySingleOrDefault<long>(
            "SELECT Id FROM ContentVersion WHERE Version = @Version",
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
        while (offset < buf.Length)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset, 4).Span);

            if (index < 0 || index >= entriesCount)
                return BadRequest("Out of bounds manifest index");

            if (bits[index])
                return BadRequest("Cannot request file twice");

            bits[index] = true;

            offset += 4;
        }

        var outStream = Response.Body;

        if (options.StreamCompression)
        {
            var zStdCompressStream = new ZStdCompressStream(outStream);
            zStdCompressStream.Context.SetParameter(
                ZSTD_cParameter.ZSTD_c_compressionLevel,
                options.StreamCompressionLevel);

            outStream = zStdCompressStream;
            Response.Headers.ContentEncoding = "zstd";
        }

        var preCompressed = true;

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

            using var stmt =
                con.Handle!.Prepare(
                    "SELECT c.Compression, c.Size, c.Id " +
                    "FROM ContentManifestEntry cme " +
                    "INNER JOIN Content c on c.Id = cme.ContentId " +
                    "WHERE cme.VersionId = @VersionId AND cme.ManifestIdx = @ManifestIdx");

            stmt.BindInt64(1, versionId); // @VersionId

            offset = 0;
            while (offset < buf.Length)
            {
                var index = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset, 4).Span);

                stmt.BindInt(2, index);

                if (stmt.Step() != raw.SQLITE_ROW)
                    throw new InvalidOperationException("Unable to find manifest row??");

                var compression = (ContentCompression)stmt.ColumnInt(0);
                var size = stmt.ColumnInt(1);
                var rowId = stmt.ColumnInt64(2);

                stmt.Reset();

                // _aczSawmill.Debug($"{index:D5}: {blobLength:D8} {dataOffset:D8} {dataLength:D8}");

                BinaryPrimitives.WriteInt32LittleEndian(fileHeader, size);

                if (blob == null)
                    blob = SqliteBlobStream.Open(con.Handle!, "main", "Content", "Data", rowId, true);
                else
                    blob.Reopen(rowId);

                if (preCompressed)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(
                        fileHeader.AsSpan(4, 4),
                        compression == ContentCompression.ZStd ? (int)blob.Length : 0);
                }

                await outStream.WriteAsync(fileHeader);

                await blob.CopyToAsync(outStream);

                offset += 4;
            }
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
