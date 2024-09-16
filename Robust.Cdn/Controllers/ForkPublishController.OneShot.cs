using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Controllers;

public sealed partial class ForkPublishController
{
    // Code for the "one shot" publish workflow where everything is done in a single request.

    [HttpPost]
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

        logger.LogInformation("Starting one-shot publish for fork {Fork} version {Version}", fork, request.Version);

        var httpClient = httpFactory.CreateClient();

        await using var tmpFile = CreateTempFile();

        logger.LogDebug("Downloading publish archive {Archive} to temp file", request.Archive);

        await using var response = await httpClient.GetStreamAsync(request.Archive, cancel);
        await response.CopyToAsync(tmpFile, cancel);
        tmpFile.Seek(0, SeekOrigin.Begin);

        using var archive = new ZipArchive(tmpFile, ZipArchiveMode.Read);

        logger.LogDebug("Classifying archive entries...");

        var artifacts = ClassifyEntries(forkConfig, archive.Entries, e => e.FullName);
        var clientArtifact = artifacts.SingleOrNull(art => art.artifact.Type == ArtifactType.Client);
        if (clientArtifact == null)
            return BadRequest("Client zip is missing!");

        var versionDir = buildDirectoryManager.GetBuildVersionPath(fork, request.Version);

        var metadata = new VersionMetadata { Version = request.Version, EngineVersion = request.EngineVersion };

        try
        {
            Directory.CreateDirectory(versionDir);

            var diskFiles = ExtractZipToVersionDir(artifacts, versionDir);
            var buildJson = GenerateBuildJson(diskFiles, clientArtifact.Value.artifact, metadata, fork);
            InjectBuildJsonIntoServers(diskFiles, buildJson);

            using var tx = manifestDatabase.Connection.BeginTransaction();

            AddVersionToDatabase(
                clientArtifact.Value.artifact,
                diskFiles,
                fork,
                metadata);

            tx.Commit();

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

    private Dictionary<Artifact, string> ExtractZipToVersionDir(
        List<(ZipArchiveEntry entry, Artifact artifact)> artifacts,
        string versionDir)
    {
        logger.LogDebug("Extracting artifacts to directory {Directory}", versionDir);

        var dict = new Dictionary<Artifact, string>();

        foreach (var (entry, artifact) in artifacts)
        {
            if (!ValidFileRegex.IsMatch(entry.Name))
            {
                logger.LogTrace("Skipping artifact {Name}: invalid name", entry.FullName);
                continue;
            }

            var filePath = Path.Combine(versionDir, entry.Name);
            logger.LogTrace("Extracting artifact {Name}", entry.FullName);

            using var entryStream = entry.Open();
            using var file = System.IO.File.Create(filePath);

            entryStream.CopyTo(file);
            dict.Add(artifact, filePath);
        }

        return dict;
    }
}
