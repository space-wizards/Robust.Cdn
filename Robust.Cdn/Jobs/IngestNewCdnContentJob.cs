using System.Buffers;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.DataAccessLayer;
using Robust.Cdn.Helpers;
using Robust.Cdn.Lib;
using SpaceWizards.Sodium;

namespace Robust.Cdn.Jobs;

[DisallowConcurrentExecution]
public sealed class IngestNewCdnContentJob(
    Database cdnDatabase,
    IOptions<CdnOptions> cdnOptions,
    IOptions<ManifestOptions> manifestOptions,
    ISchedulerFactory schedulerFactory,
    BuildDirectoryManager buildDirectoryManager,
    ILogger<IngestNewCdnContentJob> logger) : IJob
{
    public static readonly JobKey Key = new(nameof(IngestNewCdnContentJob));
    public const string KeyForkName = "ForkName";

    public static JobDataMap Data(string fork) => new()
    {
        { KeyForkName, fork }
    };

    public async Task Execute(IJobExecutionContext context)
    {
        var fork = context.MergedJobDataMap.GetString(KeyForkName) ?? throw new InvalidDataException();

        logger.LogInformation("Ingesting new versions for fork: {Fork}", fork);

        var forkConfig = manifestOptions.Value.Forks[fork];

        cdnDatabase.StartTransaction();

        List<string> newVersions;
        try
        {
            newVersions = FindNewVersions(fork);

            if (newVersions.Count == 0)
                return;

            IngestNewVersions(
                fork,
                newVersions,
                forkConfig,
                context.CancellationToken
            );

            logger.LogDebug("Committing database");

            cdnDatabase.ReleaseContentBlob();
            cdnDatabase.Commit();
        }
        finally
        {
            cdnDatabase.Dispose();
        }

        // Queue manifest available job
        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(
            MakeNewManifestVersionsAvailableJob.Key,
            MakeNewManifestVersionsAvailableJob.Data(fork, newVersions));
    }

    private void IngestNewVersions(
        string fork,
        List<string> newVersions,
        ManifestForkOptions forkConfig,
        CancellationToken cancel)
    {
        var cdnOpts = cdnOptions.Value;

        var forkId = cdnDatabase.EnsureForkCreated(fork);

        var hash = new byte[32];

        var readBuffer = ArrayPool<byte>.Shared.Rent(1024);
        var compressBuffer = ArrayPool<byte>.Shared.Rent(1024);

        using var compressor = new ZStdCompressionContext();

        try
        {
            var versionIdx = 0;
            foreach (var version in newVersions)
            {
                if (versionIdx % 5 == 0)
                {
                    logger.LogDebug("Doing interim commit");

                    cdnDatabase.ReleaseContentBlob();
                    cdnDatabase.Commit();
                    cdnDatabase.StartTransaction();
                }

                cancel.ThrowIfCancellationRequested();

                logger.LogInformation("Ingesting new version: {Version}", version);

                var versionId = cdnDatabase.InsertContentVersionAndGetId(forkId, version);


                var zipFilePath = buildDirectoryManager.GetBuildVersionFilePath(
                    fork,
                    version,
                    forkConfig.ClientZipName + ".zip");

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
                    var contentId = cdnDatabase.FindContentByHash(hash);
                    if (contentId == null)
                    {
                        // Don't have this blob yet, add a new one!
                        newBlobCount += 1;

                        ReadOnlySpan<byte> writeData;
                        var compression = ContentCompression.None;

                        // Try compression maybe.
                        if (cdnOpts.BlobCompress)
                        {
                            BufferHelpers.EnsurePooledBuffer(
                                ref compressBuffer,
                                ArrayPool<byte>.Shared,
                                ZStd.CompressBound(dataLength));

                            var compressedLength = compressor.Compress(
                                compressBuffer,
                                readData,
                                cdnOpts.BlobCompressLevel);

                            if (compressedLength + cdnOpts.BlobCompressSavingsThreshold < dataLength)
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

                        // Insert blob database and write its data.
                        contentId = cdnDatabase.InsertContent(hash, dataLength, compression, writeData.Length);
                        cdnDatabase.WriteContentBlob(contentId.Value, writeData);
                    }

                    // Insert into ContentManifestEntry
                    cdnDatabase.InsertContentManifestEntry(versionId, idx, contentId.Value);

                    // Write manifest entry.
                    manifestWriter.Write($"{Convert.ToHexString(hash)} {entry.FullName}\n");

                    idx += 1;
                }

                logger.LogDebug("Ingested {NewBlobCount} new blobs", newBlobCount);

                // Handle manifest hashing and compression.
                {
                    manifestWriter.Flush();
                    manifestStream.Position = 0;

                    var manifestData = manifestStream.GetBuffer().AsSpan(0, (int)manifestStream.Length);

                    var manifestHash = CryptoGenericHashBlake2B.Hash(32, manifestData, ReadOnlySpan<byte>.Empty);

                    logger.LogDebug("New manifest hash: {ManifestHash}", Convert.ToHexString(manifestHash));

                    BufferHelpers.EnsurePooledBuffer(
                        ref compressBuffer,
                        ArrayPool<byte>.Shared,
                        ZStd.CompressBound(manifestData.Length));

                    var compressedLength = compressor.Compress(
                        compressBuffer,
                        manifestData,
                        cdnOpts.ManifestCompressLevel);

                    var compressedData = compressBuffer.AsSpan(0, compressedLength);

                    cdnDatabase.UpdateContentVersionData(versionId, manifestHash, compressedLength);
                    cdnDatabase.WriteManifestBlob(versionId, compressedData);
                }

                // Calculate CountBlobsDeduplicated on ContentVersion
                cdnDatabase.RefreshCountDistinctBlobs(versionId);

                versionIdx += 1;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(compressBuffer);
        }
    }

    private List<string> FindNewVersions(string fork)
    {
        var newVersions = new List<(string, DateTime)>();

        var dir = buildDirectoryManager.GetForkPath(fork);
        if (!Directory.Exists(dir))
            return [];

        foreach (var versionDirectory in Directory.EnumerateDirectories(dir))
        {
            var createdTime = Directory.GetLastWriteTime(versionDirectory);
            var version = Path.GetFileName(versionDirectory);

            logger.LogTrace("Found version directory: {VersionDir}, write time: {WriteTime}", versionDirectory,
                createdTime);

            if (cdnDatabase.IsVersionExisting(version))
            {
                // Already have version, skip.
                logger.LogTrace("Already have version: {Version}", version);
                continue;
            }

            var clientZipName = manifestOptions.Value.Forks[fork].ClientZipName + ".zip";

            if (!File.Exists(Path.Combine(versionDirectory, clientZipName)))
            {
                logger.LogWarning("On-disk version is missing client zip: {Version}", version);
                continue;
            }

            newVersions.Add((version, createdTime));
            logger.LogTrace("Found new version: {Version}", version);
        }

        return newVersions.OrderByDescending(x => x.Item2).Select(x => x.Item1).ToList();
    }
}
