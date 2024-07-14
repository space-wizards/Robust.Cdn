using System.Diagnostics.CodeAnalysis;
using System.Net.Mime;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Controllers;

/// <summary>
/// Functionality for server manifests managed by the CDN.
/// This covers the server build manifest as well as the download endpoint.
/// </summary>
[ApiController]
[Route("/fork/{fork}")]
public sealed class ForkManifestController(
    ManifestDatabase database,
    BuildDirectoryManager buildDirectoryManager,
    IOptions<ManifestOptions> manifestOptions)
    : ControllerBase
{
    [HttpGet("manifest")]
    public IActionResult GetManifest(string fork)
    {
        if (!TryCheckBasicAuth(fork, out var errorResult))
            return errorResult;

        var rowId = database.Connection.QuerySingleOrDefault<long>(
            "SELECT ROWID FROM Fork WHERE Name == @Fork AND ServerManifestCache IS NOT NULL",
            new { Fork = fork });

        if (rowId == 0)
            return NotFound();

        var stream = SqliteBlobStream.Open(
            database.Connection.Handle!,
            "main",
            "Fork",
            "ServerManifestCache",
            rowId,
            false);

        return File(stream, MediaTypeNames.Application.Json);
    }

    [HttpGet("version/{version}/file/{file}")]
    public IActionResult GetFile(
        string fork,
        string version,
        string file)
    {
        // Just safety shit here.
        if (file.Contains('/') || file == ".." || file == ".")
            return BadRequest();

        if (!TryCheckBasicAuth(fork, out var errorResult))
            return errorResult;

        var versionExists = database.Connection.QuerySingleOrDefault<bool>("""
            SELECT 1
            FROM ForkVersion, Fork
            WHERE ForkVersion.Name = @Version
              AND Fork.Name = @Fork
              AND Fork.Id = ForkVersion.ForkId
            """, new { Fork = fork, Version = version });

        if (!versionExists)
            return NotFound();

        var disk = buildDirectoryManager.GetBuildVersionFilePath(fork, version, file);

        return PhysicalFile(disk, MediaTypeNames.Application.Zip);
    }

    private bool TryCheckBasicAuth(
        string fork,
        [NotNullWhen(false)] out IActionResult? errorResult)
    {
        return TryCheckBasicAuth(HttpContext, manifestOptions.Value, fork, out errorResult);
    }

    internal static bool TryCheckBasicAuth(
        HttpContext httpContext,
        ManifestOptions manifestOptions,
        string fork,
        [NotNullWhen(false)] out IActionResult? errorResult)
    {
        if (!manifestOptions.Forks.TryGetValue(fork, out var forkConfig))
        {
            errorResult = new NotFoundObjectResult("Fork does not exist");
            return false;
        }

        if (!forkConfig.Private)
        {
            errorResult = null;
            return true;
        }

        return AuthorizationUtility.CheckBasicAuth(
            httpContext,
            $"fork_{fork}",
            a => forkConfig.PrivateUsers.GetValueOrDefault(a),
            out _,
            out errorResult);
    }
}
