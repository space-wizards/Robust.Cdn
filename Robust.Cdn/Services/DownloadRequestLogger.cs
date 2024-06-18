using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Options;
using Robust.Cdn.Helpers;
using SpaceWizards.Sodium;

namespace Robust.Cdn.Services;

public sealed class DownloadRequestLogger : BackgroundService
{
    private readonly IOptions<CdnOptions> _options;
    private readonly ILogger<DownloadRequestLogger> _logger;
    private readonly ChannelReader<RequestLog> _channelReader;
    private readonly ChannelWriter<RequestLog> _channelWriter;

    public DownloadRequestLogger(
        IServiceScopeFactory scopeFactory,
        IOptions<CdnOptions> options,
        ILogger<DownloadRequestLogger> logger)
    {
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
                {
                    _logger.LogWarning("Database request logging has been removed");
                    break;
                }
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
