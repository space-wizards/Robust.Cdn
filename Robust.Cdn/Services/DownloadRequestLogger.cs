using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Options;
using Robust.Cdn.Helpers;
using SpaceWizards.Sodium;

namespace Robust.Cdn.Services;

public sealed class DownloadRequestLogger : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<CdnOptions> _options;
    private readonly ILogger<DownloadRequestLogger> _logger;
    private readonly ChannelReader<RequestLog> _channelReader;
    private readonly ChannelWriter<RequestLog> _channelWriter;

    public DownloadRequestLogger(
        IServiceScopeFactory scopeFactory,
        IOptions<CdnOptions> options,
        ILogger<DownloadRequestLogger> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;

        var channel = Channel.CreateBounded<RequestLog>(new BoundedChannelOptions(32) { SingleReader = true });
        _channelReader = channel.Reader;
        _channelWriter = channel.Writer;
    }

    public async ValueTask QueueLog(RequestLog entry)
    {
        await _channelWriter.WriteAsync(entry);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            await _channelReader.WaitToReadAsync(stoppingToken);

            try
            {
                var storage = _options.Value.LogRequestStorage;
                if (storage == RequestLogStorage.Database)
                    WriteLogsDatabase();
                else if (storage == RequestLogStorage.Console)
                    WriteLogsConsole();
                else
                    _logger.LogError("Unsupported LogRequestStorage configured: ${Format}", storage);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while logging requests");
            }
        }

        // ReSharper disable once FunctionNeverReturns
    }

    private void WriteLogsDatabase()
    {
        using var scope = _scopeFactory.CreateScope();

        var connection = scope.ServiceProvider.GetRequiredService<Database>().Connection;
        using var transaction = connection.BeginTransaction();

        var countWritten = 0;
        while (_channelReader.TryRead(out var entry))
        {
            var hash = CryptoGenericHashBlake2B.Hash(32, entry.RequestData.Span, ReadOnlySpan<byte>.Empty);
            var blobRowId = connection.QuerySingleOrDefault<long>("SELECT Id FROM RequestLogBlob WHERE Hash = @Hash",
                new
                {
                    Hash = hash
                });

            if (blobRowId == 0)
            {
                blobRowId = connection.ExecuteScalar<long>(
                    "INSERT INTO RequestLogBlob (Hash, Data) VALUES (@Hash, zeroblob(@DataSize)) RETURNING Id",
                    new
                    {
                        Hash = hash,
                        DataSize = entry.RequestData.Length
                    });

                using var blob = SqliteBlobStream.Open(
                    connection.Handle!,
                    "main", "RequestLogBlob", "Data",
                    blobRowId,
                    true);

                blob.Write(entry.RequestData.Span);
            }

            connection.Execute(
                "INSERT INTO RequestLog (Time, Compression, Protocol, BytesSent, VersionId, BlobId) " +
                "VALUES (@Time, @Compression, @Protocol, @BytesSent, @VersionId, @BlobId)",
                new
                {
                    entry.Time,
                    entry.Compression,
                    entry.Protocol,
                    entry.VersionId,
                    entry.BytesSent,
                    BlobId = blobRowId
                });

            countWritten += 1;
        }

        transaction.Commit();
        _logger.LogDebug("Wrote {CountWritten} log entries to disk", countWritten);
    }

    private void WriteLogsConsole()
    {
        var countWritten = 0;
        while (_channelReader.TryRead(out var entry))
        {
            var hash = CryptoGenericHashBlake2B.Hash(32, entry.RequestData.Span, ReadOnlySpan<byte>.Empty);
            _logger.LogInformation("RequestLog {Time} {Compression} {Protocol} {VersionId} {BytesSent} {DataSize} {Hash}",
                entry.Time.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                entry.Compression,
                entry.Protocol,
                entry.VersionId,
                entry.BytesSent,
                entry.RequestData.Length,
                Convert.ToHexString(hash));
            countWritten += 1;
        }
        _logger.LogDebug("Wrote {CountWritten} log entries to console", countWritten);
    }

    public sealed record RequestLog(
        ReadOnlyMemory<byte> RequestData,
        RequestLogCompression Compression,
        int Protocol,
        DateTime Time,
        long VersionId,
        long BytesSent);

    [Flags]
    public enum RequestLogCompression
    {
        None = 0,
        Stream = 1,
        PreCompress = 2,
        PreAndStream = 3,
    }
}
