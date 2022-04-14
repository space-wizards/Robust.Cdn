using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Robust.Cdn.Helpers;
using SpaceWizards.Sodium;
using SQLitePCL;

namespace Robust.Cdn.Services;

public sealed class DataLoader : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<CdnOptions> _options;
    private readonly ILogger<DataLoader> _logger;

    private readonly ChannelReader<object?> _channelReader;
    private readonly ChannelWriter<object?> _channelWriter;

    public DataLoader(IServiceScopeFactory scopeFactory, IOptions<CdnOptions> options, ILogger<DataLoader> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;

        var channel = Channel.CreateBounded<object?>(new BoundedChannelOptions(1) { SingleReader = true });
        _channelReader = channel.Reader;
        _channelWriter = channel.Writer;
    }

    public async ValueTask QueueUpdateVersions()
    {
        await _channelWriter.WriteAsync(null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _channelWriter.TryWrite(null);

        // Idk if there's a better way but make sure we don't hold up startup.
        await Task.Delay(1000, stoppingToken);

        while (true)
        {
            await _channelReader.ReadAsync(stoppingToken);

            _logger.LogInformation("Updating versions");

            try
            {
                Update(stoppingToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while loading new content versions");
            }
        }

        // ReSharper disable once FunctionNeverReturns
    }

    private void Update(CancellationToken cancel)
    {
        using var scope = _scopeFactory.CreateScope();

        var options = _options.Value;
        var connection = scope.ServiceProvider.GetRequiredService<Database>().Connection;
        var transaction = connection.BeginTransaction();

        var newVersions = FindNewVersions(connection);

        if (newVersions.Count == 0)
            return;

        using var stmtLookupContent = connection.Handle!.Prepare("SELECT Id FROM Content WHERE Hash = ?");
        using var stmtInsertContent = connection.Handle!.Prepare(
            "INSERT INTO Content (Hash, Size, Compression, Data) " +
            "VALUES (@Hash, @Size, @Compression, @Data) " +
            "RETURNING Id");

        using var stmtInsertContentManifestEntry = connection.Handle!.Prepare(
            "INSERT INTO ContentManifestEntry (VersionId, ManifestIdx, ContentId) " +
            "VALUES (@VersionId, @ManifestIdx, @ContentId) ");

        var hash = new byte[32];

        var readBuffer = ArrayPool<byte>.Shared.Rent(1024);
        var compressBuffer = ArrayPool<byte>.Shared.Rent(1024);

        using var compressor = new ZStdCompressionContext();
        SqliteBlobStream? blob = null;

        try
        {
            var versionIdx = 0;
            foreach (var version in newVersions)
            {
                if (versionIdx % 5 == 0)
                {
                    _logger.LogDebug("Doing interim commit");

                    blob?.Dispose();
                    blob = null;

                    transaction.Commit();
                    transaction = connection.BeginTransaction();
                }

                cancel.ThrowIfCancellationRequested();

                _logger.LogInformation("Ingesting new version: {Version}", version);

                var versionId = connection.ExecuteScalar<long>(
                    "INSERT INTO ContentVersion (Version, TimeAdded, ManifestHash, ManifestData, CountDistinctBlobs) " +
                    "VALUES (@Version, datetime('now'), zeroblob(0), zeroblob(0), 0) " +
                    "RETURNING Id",
                    new { Version = version });

                stmtInsertContentManifestEntry.BindInt64(1, versionId);

                var zipFilePath = Path.Combine(options.VersionDiskPath, version, options.ClientZipName);

                using var zipFile = ZipFile.OpenRead(zipFilePath);

                // TODO: hash incrementally without buffering in-memory
                var manifestStream = new MemoryStream();
                var manifestWriter = new StreamWriter(manifestStream, new UTF8Encoding(false));
                manifestWriter.Write("Robust Content Manifest 1\n");

                var newBlobCount = 0;

                var idx = 0;
                foreach (var entry in zipFile.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
                {
                    cancel.ThrowIfCancellationRequested();

                    // Ignore directory entries.
                    if (entry.Name == "")
                        continue;

                    var dataLength = (int)entry.Length;

                    BufferHelpers.EnsurePooledBuffer(ref readBuffer, ArrayPool<byte>.Shared, dataLength);

                    var readData = readBuffer.AsSpan(0, dataLength);
                    using (var stream = entry.Open())
                    {
                        stream.ReadExact(readData);
                    }

                    // Hash the data.
                    CryptoGenericHashBlake2B.Hash(hash, readData, ReadOnlySpan<byte>.Empty);

                    // Look up if we already have this blob.
                    stmtLookupContent.BindBlob(1, hash);

                    long contentId;
                    if (stmtLookupContent.Step() == raw.SQLITE_DONE)
                    {
                        stmtLookupContent.Reset();

                        // Don't have this blob yet, add a new one!
                        newBlobCount += 1;

                        ReadOnlySpan<byte> writeData;
                        var compression = ContentCompression.None;

                        // Try compression maybe.
                        if (options.BlobCompress)
                        {
                            BufferHelpers.EnsurePooledBuffer(
                                ref compressBuffer,
                                ArrayPool<byte>.Shared,
                                ZStd.CompressBound(dataLength));

                            var compressedLength = compressor.Compress(
                                compressBuffer,
                                readData,
                                options.BlobCompressLevel);

                            if (compressedLength + options.BlobCompressSavingsThreshold < dataLength)
                            {
                                compression = ContentCompression.ZStd;
                                writeData = compressBuffer.AsSpan(0, compressedLength);
                            }
                            else
                            {
                                writeData = readData;
                            }
                        }
                        else
                        {
                            writeData = readData;
                        }

                        // Insert blob database.

                        stmtInsertContent.BindBlob(1, hash); // @Hash
                        stmtInsertContent.BindInt(2, dataLength); // @Size
                        stmtInsertContent.BindInt(3, (int)compression); // @Compression
                        stmtInsertContent.BindZeroBlob(4, writeData.Length); // @Data

                        stmtInsertContent.Step();

                        contentId = stmtInsertContent.ColumnInt64(0);

                        stmtInsertContent.Reset();

                        if (blob == null)
                        {
                            blob = SqliteBlobStream.Open(
                                connection.Handle!,
                                "main",
                                "Content",
                                "Data",
                                contentId,
                                true);
                        }
                        else
                        {
                            blob.Reopen(contentId);
                        }

                        blob.Write(writeData);
                    }
                    else
                    {
                        contentId = stmtLookupContent.ColumnInt64(0);

                        stmtLookupContent.Reset();
                    }

                    // Insert into ContentManifestEntry
                    stmtInsertContentManifestEntry.BindInt64(2, idx); // @ManifestIdx
                    stmtInsertContentManifestEntry.BindInt64(3, contentId); // @ContentId

                    stmtInsertContentManifestEntry.Step();
                    stmtInsertContentManifestEntry.Reset();

                    // Write manifest entry.
                    manifestWriter.Write($"{Convert.ToHexString(hash)} {entry.FullName}\n");

                    idx += 1;
                }

                _logger.LogDebug("Ingested {NewBlobCount} new blobs", newBlobCount);

                // Handle manifest hashing and compression.
                {
                    manifestWriter.Flush();
                    manifestStream.Position = 0;

                    var manifestData = manifestStream.GetBuffer().AsSpan(0, (int)manifestStream.Length);

                    var manifestHash = CryptoGenericHashBlake2B.Hash(32, manifestData, ReadOnlySpan<byte>.Empty);

                    _logger.LogDebug("New manifest hash: {ManifestHash}", Convert.ToHexString(manifestHash));

                    BufferHelpers.EnsurePooledBuffer(
                        ref compressBuffer,
                        ArrayPool<byte>.Shared,
                        ZStd.CompressBound(manifestData.Length));

                    var compressedLength = compressor.Compress(
                        compressBuffer,
                        manifestData,
                        options.ManifestCompressLevel);

                    var compressedData = compressBuffer.AsSpan(0, compressedLength);

                    connection.Execute(
                        "UPDATE ContentVersion " +
                        "SET ManifestHash = @ManifestHash, ManifestData = zeroblob(@ManifestDataSize) " +
                        "WHERE Id = @VersionId",
                        new
                        {
                            VersionId = versionId,
                            ManifestHash = manifestHash,
                            ManifestDataSize = compressedLength
                        });

                    using var manifestBlob = SqliteBlobStream.Open(
                        connection.Handle!,
                        "main",
                        "ContentVersion",
                        "ManifestData",
                        versionId,
                        true);

                    manifestBlob.Write(compressedData);
                }

                // Calculate CountBlobsDeduplicated on ContentVersion

                connection.Execute(
                    "UPDATE ContentVersion AS cv " +
                    "SET CountDistinctBlobs = " +
                    "   (SELECT COUNT(DISTINCT cme.ContentId) FROM ContentManifestEntry cme WHERE cme.VersionId = cv.Id) " +
                    "WHERE cv.Id = @VersionId",
                    new { VersionId = versionId }
                );

                versionIdx += 1;
            }
        }
        finally
        {
            blob?.Dispose();

            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(compressBuffer);
        }

        transaction.Commit();

        GC.Collect();
    }

    private List<string> FindNewVersions(SqliteConnection con)
    {
        using var stmtCheckVersion = con.Handle!.Prepare("SELECT 1 FROM ContentVersion WHERE Version = ?");

        var newVersions = new List<string>();

        foreach (var versionDirectory in Directory.EnumerateDirectories(_options.Value.VersionDiskPath))
        {
            var version = Path.GetFileName(versionDirectory);

            stmtCheckVersion.Reset();
            stmtCheckVersion.BindString(1, version);

            if (stmtCheckVersion.Step() == raw.SQLITE_ROW)
            {
                // Already have version, skip.
                _logger.LogTrace("Already have version: {Version}", version);
                continue;
            }

            if (!File.Exists(Path.Combine(versionDirectory, _options.Value.ClientZipName)))
            {
                _logger.LogWarning("On-disk version is missing client zip: {Version}", version);
                continue;
            }

            newVersions.Add(version);
            _logger.LogTrace("Found new version: {Version}", version);
        }

        return newVersions;
    }
}
