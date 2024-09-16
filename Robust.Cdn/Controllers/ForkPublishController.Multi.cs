using Dapper;
using Microsoft.AspNetCore.Mvc;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Controllers;

public sealed partial class ForkPublishController
{
    // Code for "multi-request" publishes.
    // i.e. start, followed by files, followed by finish call.

    [HttpPost("start")]
    public async Task<IActionResult> MultiPublishStart(
        string fork,
        [FromBody] PublishMultiRequest request,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(fork, out _, out var failureResult))
            return failureResult;

        baseUrlManager.ValidateBaseUrl();

        if (!ValidVersionRegex.IsMatch(request.Version))
            return BadRequest("Invalid version name");

        if (VersionAlreadyExists(fork, request.Version))
            return Conflict("Version already exists");

        var dbCon = manifestDatabase.Connection;

        await using var tx = await dbCon.BeginTransactionAsync(cancel);

        logger.LogInformation("Starting multi publish for fork {Fork} version {Version}", fork, request.Version);

        var hasExistingPublish = dbCon.QuerySingleOrDefault<bool>(
            "SELECT 1 FROM PublishInProgress WHERE Version = @Version ",
            new { request.Version });
        if (hasExistingPublish)
        {
            // If a publish with this name already exists we abort it and start again.
            // We do this so you can "just" retry a mid-way-failed publish without an extra API call required.

            logger.LogWarning("Already had an in-progress publish for this version, aborting it and restarting.");
            publishManager.AbortMultiPublish(fork, request.Version, tx, commit: false);
        }

        var forkId = dbCon.QuerySingle<int>("SELECT Id FROM Fork WHERE Name = @Name", new { Name = fork });

        await dbCon.ExecuteAsync("""
            INSERT INTO PublishInProgress (Version, ForkId, StartTime, EngineVersion)
            VALUES (@Version, @ForkId, @StartTime, @EngineVersion)
            """,
            new
            {
                request.Version,
                request.EngineVersion,
                ForkId = forkId,
                StartTime = DateTime.UtcNow
            });

        var versionDir = buildDirectoryManager.GetBuildVersionPath(fork, request.Version);
        Directory.CreateDirectory(versionDir);

        await tx.CommitAsync(cancel);

        logger.LogInformation("Multi publish initiated. Waiting for subsequent API requests...");

        return NoContent();
    }

    [HttpPost("file")]
    [RequestSizeLimit(2048L * 1024 * 1024)]
    public async Task<IActionResult> MultiPublishFile(
        string fork,
        [FromHeader(Name = "Robust-Cdn-Publish-File")]
        string fileName,
        [FromHeader(Name = "Robust-Cdn-Publish-Version")]
        string version,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(fork, out _, out var failureResult))
            return failureResult;

        if (!ValidFileRegex.IsMatch(fileName))
            return BadRequest("Invalid artifact file name");

        var dbCon = manifestDatabase.Connection;
        await using var tx = await dbCon.BeginTransactionAsync(cancel);

        var forkId = dbCon.QuerySingle<int>("SELECT Id FROM Fork WHERE Name = @Name", new { Name = fork });
        var versionId = dbCon.QuerySingleOrDefault<int?>("""
            SELECT Id
            FROM PublishInProgress
            WHERE Version = @Name AND ForkId = @Fork
            """,
            new { Name = version, Fork = forkId });

        if (versionId == null)
            return NotFound("Unknown in-progress version");

        var versionDir = buildDirectoryManager.GetBuildVersionPath(fork, version);
        var filePath = Path.Combine(versionDir, fileName);

        if (System.IO.File.Exists(filePath))
            return Conflict("File already published");

        logger.LogDebug("Receiving file {FileName} for multi-publish version {Version}", fileName, version);

        await using var file = System.IO.File.Create(filePath, 4096, FileOptions.Asynchronous);

        await Request.Body.CopyToAsync(file, cancel);

        logger.LogDebug("Successfully Received file {FileName}", fileName);

        return NoContent();
    }

    [HttpPost("finish")]
    public async Task<IActionResult> MultiPublishFinish(
        string fork,
        [FromBody] PublishFinishRequest request,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(fork, out var forkConfig, out var failureResult))
            return failureResult;

        var dbCon = manifestDatabase.Connection;
        await using var tx = await dbCon.BeginTransactionAsync(cancel);

        var forkId = dbCon.QuerySingle<int>("SELECT Id FROM Fork WHERE Name = @Name", new { Name = fork });
        var versionMetadata = dbCon.QuerySingleOrDefault<VersionMetadata>("""
            SELECT Version, EngineVersion
            FROM PublishInProgress
            WHERE Version = @Name AND ForkId = @Fork
            """,
            new { Name = request.Version, Fork = forkId });

        if (versionMetadata == null)
            return NotFound("Unknown in-progress version");

        logger.LogInformation("Finishing multi publish {Version} for fork {Fork}", request.Version, fork);

        var versionDir = buildDirectoryManager.GetBuildVersionPath(fork, request.Version);

        logger.LogDebug("Classifying entries...");

        var artifacts = ClassifyEntries(
            forkConfig,
            Directory.GetFiles(versionDir),
            item => Path.GetRelativePath(versionDir, item));

        var clientArtifact = artifacts.SingleOrNull(art => art.artifact.Type == ArtifactType.Client);
        if (clientArtifact == null)
        {
            publishManager.AbortMultiPublish(fork, request.Version, tx, commit: true);
            return UnprocessableEntity("Publish failed: no client zip was provided");
        }

        var diskFiles = artifacts.ToDictionary(i => i.artifact, i => i.key);

        var buildJson = GenerateBuildJson(diskFiles, clientArtifact.Value.artifact, versionMetadata, fork);
        InjectBuildJsonIntoServers(diskFiles, buildJson);

        AddVersionToDatabase(clientArtifact.Value.artifact, diskFiles, fork, versionMetadata);

        dbCon.Execute(
            "DELETE FROM PublishInProgress WHERE Version = @Name AND ForkId = @Fork",
            new { Name = request.Version, Fork = forkId });

        tx.Commit();

        await QueueIngestJobAsync(fork);

        logger.LogInformation("Publish succeeded!");

        return NoContent();
    }

    public sealed class PublishMultiRequest
    {
        public required string Version { get; set; }
        public required string EngineVersion { get; set; }
    }

    public sealed class PublishFinishRequest
    {
        public required string Version { get; set; }
    }
}
