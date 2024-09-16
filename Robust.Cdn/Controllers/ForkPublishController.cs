using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;
using Robust.Cdn.Jobs;
using Robust.Cdn.Services;
using SpaceWizards.Sodium;

namespace Robust.Cdn.Controllers;

/// <summary>
/// Implements the publish endpoint used to receive new builds from CI.
/// </summary>
/// <remarks>
/// <para>
/// The actual build content is provided as a single zip artifact containing server and client files.
/// This file is pulled from a URL by the CDN, not pushed.
/// This should be compatible with systems such as GitHub Actions artifacts.
/// </para>
/// <para>
/// The artifacts are pulled and written to disk. The client manifest information is built and injected into the stored
/// server builds, filling out the <c>build.json</c> file.
/// </para>
/// <para>
/// After publish, the game client CDN is notified to ingest the client files.
/// Builds are only marked available for servers after the CDN has finished ingesting them.
/// </para>
/// </remarks>
[ApiController]
[Route("/fork/{fork}/publish")]
public sealed partial class ForkPublishController(
    ForkAuthHelper authHelper,
    IHttpClientFactory httpFactory,
    ManifestDatabase manifestDatabase,
    ISchedulerFactory schedulerFactory,
    BaseUrlManager baseUrlManager,
    BuildDirectoryManager buildDirectoryManager,
    PublishManager publishManager,
    ILogger<ForkPublishController> logger)
    : ControllerBase
{
    private static readonly Regex ValidVersionRegex = ValidVersionRegexBuilder();
    private static readonly Regex ValidFileRegex = ValidFileRegexBuilder();

    public const string PublishFetchHttpClient = "PublishFetch";

    private bool VersionAlreadyExists(string fork, string version)
    {
        return manifestDatabase.Connection.QuerySingleOrDefault<bool>(
            """
            SELECT 1
            FROM Fork, ForkVersion
            WHERE Fork.Id = ForkVersion.ForkId
              AND Fork.Name = @ForkName
              AND ForkVersion.Name = @ForkVersion
            """, new
            {
                ForkName = fork,
                ForkVersion = version
            });
    }

    private List<(T key, Artifact artifact)> ClassifyEntries<T>(
        ManifestForkOptions forkConfig,
        IEnumerable<T> items,
        Func<T, string> getName)
    {
        var list = new List<(T, Artifact)>();

        foreach (var item in items)
        {
            var name = getName(item);
            var artifact = ClassifyEntry(forkConfig, name);

            if (artifact == null)
                continue;

            logger.LogDebug(
                "Artifact entry {Name}: Type {Type} Platform {Platform}",
                name,
                artifact.Type,
                artifact.Platform);

            list.Add((item, artifact));
        }

        return list;
    }

    private static Artifact? ClassifyEntry(ManifestForkOptions forkConfig, string name)
    {
        if (name == $"{forkConfig.ClientZipName}.zip")
            return new Artifact { Type = ArtifactType.Client };

        if (name.StartsWith(forkConfig.ServerZipName) && name.EndsWith(".zip"))
        {
            var rid = name[forkConfig.ServerZipName.Length..^".zip".Length];
            return new Artifact
            {
                Platform = rid,
                Type = ArtifactType.Server
            };
        }

        return null;
    }

    private MemoryStream GenerateBuildJson(
        Dictionary<Artifact, string> diskFiles,
        Artifact clientArtifact,
        VersionMetadata metadata,
        string forkName)
    {
        logger.LogDebug("Generating build.json contents");

        var diskPath = diskFiles[clientArtifact];

        var diskFileName = Path.GetFileName(diskPath);
        using var file = System.IO.File.OpenRead(diskPath);

        // Hash zip file
        var hash = Convert.ToHexString(SHA256.HashData(file));

        // Hash manifest
        var manifestHash = Convert.ToHexString(GenerateManifestHash(file));

        logger.LogDebug("Client zip hash is {ZipHash}, manifest hash is {ManifestHash}", hash, manifestHash);

        var data = new Dictionary<string, string>
        {
            { "download", baseUrlManager.MakeBuildInfoUrl($"fork/{{FORK_ID}}/version/{{FORK_VERSION}}/file/{diskFileName}") },
            { "version", metadata.Version },
            { "hash", hash },
            { "fork_id", forkName },
            { "engine_version", metadata.EngineVersion },
            { "manifest_url", baseUrlManager.MakeBuildInfoUrl("fork/{FORK_ID}/version/{FORK_VERSION}/manifest") },
            { "manifest_download_url", baseUrlManager.MakeBuildInfoUrl("fork/{FORK_ID}/version/{FORK_VERSION}/download") },
            { "manifest_hash", manifestHash }
        };

        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, data);

        stream.Position = 0;
        return stream;
    }

    private byte[] GenerateManifestHash(Stream zipFile)
    {
        using var zip = new ZipArchive(zipFile, ZipArchiveMode.Read);

        var manifest = new MemoryStream();
        var writer = new StreamWriter(manifest, new UTF8Encoding(false), leaveOpen: true);

        writer.Write("Robust Content Manifest 1\n");

        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            // Ignore directory entries.
            if (entry.Name == "")
                continue;

            var hash = GetZipEntryBlake2B(entry);
            writer.Write($"{Convert.ToHexString(hash)} {entry.FullName}\n");
        }

        writer.Dispose();

        return CryptoGenericHashBlake2B.Hash(
            CryptoGenericHashBlake2B.Bytes,
            manifest.AsSpan(),
            ReadOnlySpan<byte>.Empty);
    }

    private static byte[] GetZipEntryBlake2B(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();

        return HashHelper.HashBlake2B(stream);
    }

    private void InjectBuildJsonIntoServers(Dictionary<Artifact, string> diskFiles, MemoryStream buildJson)
    {
        logger.LogDebug("Adding build.json to server builds");

        foreach (var (artifact, diskPath) in diskFiles)
        {
            if (artifact.Type != ArtifactType.Server)
                continue;

            logger.LogTrace("Adding build.json to build {ServerBuildFileName}", diskPath);

            using var zipFile = System.IO.File.Open(diskPath, FileMode.Open);
            using var zip = new ZipArchive(zipFile, ZipArchiveMode.Update);

            if (zip.GetEntry("build.json") is { } existing)
            {
                logger.LogDebug("Zip {ServerBuildFileName} had existing build.json, deleting", diskPath);
                existing.Delete();
            }

            var buildJsonEntry = zip.CreateEntry("build.json");
            using var entryStream = buildJsonEntry.Open();

            buildJson.CopyTo(entryStream);
            buildJson.Position = 0;
        }
    }

    private void AddVersionToDatabase(
        Artifact clientArtifact,
        Dictionary<Artifact, string> diskFiles,
        string fork,
        VersionMetadata metadata)
    {
        logger.LogDebug("Adding new version to database");

        var dbCon = manifestDatabase.Connection;

        var forkId = dbCon.QuerySingle<int>("SELECT Id FROM Fork WHERE Name = @Name", new { Name = fork });

        var (clientName, clientSha256, _) = GetFileNameSha256Pair(diskFiles[clientArtifact]);

        var versionId = dbCon.QuerySingle<int>("""
            INSERT INTO ForkVersion (Name, ForkId, PublishedTime, ClientFileName, ClientSha256, EngineVersion)
            VALUES (@Name, @ForkId, @PublishTime, @ClientName, @ClientSha256, @EngineVersion)
            RETURNING Id
            """,
            new
            {
                Name = metadata.Version,
                ForkId = forkId,
                ClientName = clientName,
                ClientSha256 = clientSha256,
                metadata.EngineVersion,
                PublishTime = DateTime.UtcNow
            });

        foreach (var (artifact, diskPath) in diskFiles)
        {
            if (artifact.Type != ArtifactType.Server)
                continue;

            var (serverName, serverSha256, fileSize) = GetFileNameSha256Pair(diskPath);

            dbCon.Execute("""
                INSERT INTO ForkVersionServerBuild (ForkVersionId, Platform, FileName, Sha256, FileSize)
                VALUES (@ForkVersion, @Platform, @ServerName, @ServerSha256, @FileSize)
                """,
                new
                {
                    ForkVersion = versionId,
                    artifact.Platform,
                    ServerName = serverName,
                    ServerSha256 = serverSha256,
                    FileSize = fileSize
                });
        }
    }

    private static (string name, byte[] hash, long size) GetFileNameSha256Pair(string diskPath)
    {
        using var file = System.IO.File.OpenRead(diskPath);

        return (Path.GetFileName(diskPath), SHA256.HashData(file), file.Length);
    }

    private async Task QueueIngestJobAsync(string fork)
    {
        logger.LogDebug("Notifying client CDN for ingest of new files");

        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(IngestNewCdnContentJob.Key, IngestNewCdnContentJob.Data(fork));
    }

    private static FileStream CreateTempFile()
    {
        return new FileStream(
            Path.GetTempFileName(),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.DeleteOnClose);
    }

    public sealed class PublishRequest
    {
        public required string Version { get; set; }
        public required string EngineVersion { get; set; }
        public required string Archive { get; set; }
    }

    private sealed class VersionMetadata
    {
        public required string Version { get; init; }
        public required string EngineVersion { get; set; }
    }

    // File cannot start with a dot but otherwise most shit is fair game.
    [GeneratedRegex(@"[a-zA-Z0-9\-_][a-zA-Z0-9\-_.]*")]
    private static partial Regex ValidVersionRegexBuilder();

    [GeneratedRegex(@"[a-zA-Z0-9\-_][a-zA-Z0-9\-_.]*")]
    private static partial Regex ValidFileRegexBuilder();

    private sealed class Artifact
    {
        public ArtifactType Type { get; set; }
        public string? Platform { get; set; }
    }

    private enum ArtifactType
    {
        Server,
        Client
    }
}
