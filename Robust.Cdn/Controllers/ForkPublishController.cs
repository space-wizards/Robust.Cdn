using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;
using Robust.Cdn.Jobs;
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
[Route("/fork/{fork}")]
public sealed partial class ForkPublishController(
    ForkAuthHelper authHelper,
    IHttpClientFactory httpFactory,
    IOptions<ManifestOptions> manifestOptions,
    ManifestDatabase manifestDatabase,
    ISchedulerFactory schedulerFactory,
    BaseUrlManager baseUrlManager,
    ILogger<ForkPublishController> logger)
    : ControllerBase
{
    private static readonly Regex ValidVersionRegex = MyRegex();

    public const string PublishFetchHttpClient = "PublishFetch";

    [HttpPost("publish")]
    public async Task<IActionResult> PostPublish(
        string fork,
        [FromBody] PublishRequest request,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(fork, out var forkConfig, out var failureResult))
            return failureResult;

        baseUrlManager.ValidateBaseUrl();

        if (string.IsNullOrWhiteSpace(request.Archive))
            return BadRequest("Archive is empty");

        if (!ValidVersionRegex.IsMatch(request.Version))
            return BadRequest("Invalid version name");

        if (VersionAlreadyExists(fork, request.Version))
            return Conflict("Version already exists");

        logger.LogInformation("Starting publish for fork {Fork} version {Version}", fork, request.Version);

        var httpClient = httpFactory.CreateClient();

        await using var tmpFile = CreateTempFile();

        logger.LogDebug("Downloading publish archive {Archive} to temp file", request.Archive);

        await using var response = await httpClient.GetStreamAsync(request.Archive, cancel);
        await response.CopyToAsync(tmpFile, cancel);
        tmpFile.Seek(0, SeekOrigin.Begin);

        using var archive = new ZipArchive(tmpFile, ZipArchiveMode.Read);

        logger.LogDebug("Classifying archive entries...");

        var artifacts = ClassifyEntries(forkConfig, archive);
        var clientArtifact = artifacts.SingleOrDefault(art => art.Type == ArtifactType.Client);
        if (clientArtifact == null)
            return BadRequest("Client zip is missing!");

        var versionDir = Path.Combine(manifestOptions.Value.FileDiskPath, fork, request.Version);

        try
        {
            Directory.CreateDirectory(versionDir);

            var diskFiles = ExtractZipToVersionDir(artifacts, versionDir);
            var buildJson = GenerateBuildJson(diskFiles, clientArtifact, request, fork);
            InjectBuildJsonIntoServers(diskFiles, buildJson);

            AddVersionToDatabase(clientArtifact, diskFiles, fork, request);

            await QueueIngestJobAsync(fork);

            logger.LogInformation("Publish succeeded!");

            return NoContent();
        }
        catch
        {
            // Clean up after ourselves if something goes wrong.
            Directory.Delete(versionDir, true);

            throw;
        }
    }

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

    private List<ZipArtifact> ClassifyEntries(ManifestForkOptions forkConfig, ZipArchive archive)
    {
        var list = new List<ZipArtifact>();

        foreach (var entry in archive.Entries)
        {
            var artifact = ClassifyEntry(forkConfig, entry);

            if (artifact == null)
                continue;

            logger.LogDebug(
                "Artifact entry {Name}: Type {Type} Platform {Platform}",
                entry.FullName,
                artifact.Type,
                artifact.Platform);

            list.Add(artifact);
        }

        return list;
    }

    private static ZipArtifact? ClassifyEntry(ManifestForkOptions forkConfig, ZipArchiveEntry entry)
    {
        if (entry.FullName == $"{forkConfig.ClientZipName}.zip")
            return new ZipArtifact { Entry = entry, Type = ArtifactType.Client };

        if (entry.FullName.StartsWith(forkConfig.ServerZipName) && entry.FullName.EndsWith(".zip"))
        {
            var rid = entry.FullName[forkConfig.ServerZipName.Length..^".zip".Length];
            return new ZipArtifact
            {
                Entry = entry,
                Platform = rid,
                Type = ArtifactType.Server
            };
        }

        return null;
    }

    private Dictionary<ZipArtifact, string> ExtractZipToVersionDir(List<ZipArtifact> artifacts, string versionDir)
    {
        logger.LogDebug("Extracting artifacts to directory {Directory}", versionDir);

        var dict = new Dictionary<ZipArtifact, string>();

        foreach (var artifact in artifacts)
        {
            var filePath = Path.Combine(versionDir, artifact.Entry.Name);
            logger.LogTrace("Extracting artifact {Name}", artifact.Entry.FullName);

            using var entry = artifact.Entry.Open();
            using var file = System.IO.File.Create(filePath);

            entry.CopyTo(file);
            dict.Add(artifact, filePath);
        }

        return dict;
    }

    private MemoryStream GenerateBuildJson(
        Dictionary<ZipArtifact, string> diskFiles,
        ZipArtifact clientArtifact,
        PublishRequest request,
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
            { "version", request.Version },
            { "hash", hash },
            { "fork_id", forkName },
            { "engine_version", request.EngineVersion },
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

        writer.Write("Robust Content Manifeset 1\n");

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

    private void InjectBuildJsonIntoServers(Dictionary<ZipArtifact, string> diskFiles, MemoryStream buildJson)
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
        ZipArtifact clientArtifact,
        Dictionary<ZipArtifact, string> diskFiles,
        string fork,
        PublishRequest request)
    {
        logger.LogDebug("Adding new version to database");

        var dbCon = manifestDatabase.Connection;
        using var tx = dbCon.BeginTransaction();

        var forkId = dbCon.QuerySingle<int>("SELECT Id FROM Fork WHERE Name = @Name", new { Name = fork });

        var (clientName, clientSha256) = GetFileNameSha256Pair(diskFiles[clientArtifact]);

        var versionId = dbCon.QuerySingle<int>("""
            INSERT INTO ForkVersion (Name, ForkId, PublishedTime, ClientFileName, ClientSha256, EngineVersion)
            VALUES (@Name, @ForkId, DATETIME('now'), @ClientName, @ClientSha256, @EngineVersion)
            RETURNING Id
            """,
            new
            {
                Name = request.Version,
                ForkId = forkId,
                ClientName = clientName,
                ClientSha256 = clientSha256,
                request.EngineVersion
            });

        foreach (var (artifact, diskPath) in diskFiles)
        {
            if (artifact.Type != ArtifactType.Server)
                continue;

            var (serverName, serverSha256) = GetFileNameSha256Pair(diskPath);

            dbCon.Execute("""
                INSERT INTO ForkVersionServerBuild (ForkVersionId, Platform, FileName, Sha256)
                VALUES (@ForkVersion, @Platform, @ServerName, @ServerSha256)
                """,
                new
                {
                    ForkVersion = versionId,
                    artifact.Platform,
                    ServerName = serverName,
                    ServerSha256 = serverSha256
                });
        }

        tx.Commit();
    }

    private static (string name, byte[] hash) GetFileNameSha256Pair(string diskPath)
    {
        using var file = System.IO.File.OpenRead(diskPath);

        return (Path.GetFileName(diskPath), SHA256.HashData(file));
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

    // File cannot start with a dot but otherwise most shit is fair game.
    [GeneratedRegex(@"[a-zA-Z0-9\-_][a-zA-Z0-9\-_.]*")]
    private static partial Regex MyRegex();

    private sealed class ZipArtifact
    {
        public required ZipArchiveEntry Entry { get; set; }
        public ArtifactType Type { get; set; }
        public string? Platform { get; set; }
    }

    private enum ArtifactType
    {
        Server,
        Client
    }
}
