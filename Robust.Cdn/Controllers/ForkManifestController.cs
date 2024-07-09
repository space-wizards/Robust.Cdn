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
    public IActionResult GetManifest([FromHeader(Name = "Authorization")] string? authorization, string fork)
    {
        if (!TryCheckBasicAuth(authorization, fork, out var errorResult))
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
        [FromHeader(Name = "Authorization")] string? authorization,
        string fork,
        string version,
        string file)
    {
        // Just safety shit here.
        if (file.Contains('/') || file == ".." || file == ".")
            return BadRequest();

        if (!TryCheckBasicAuth(authorization, fork, out var errorResult))
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
        string? authorization,
        string fork,
        [NotNullWhen(false)] out IActionResult? errorResult)
    {
        if (!manifestOptions.Value.Forks.TryGetValue(fork, out var forkConfig))
        {
            errorResult = NotFound("Fork does not exist");
            return false;
        }

        if (!forkConfig.Private)
        {
            errorResult = null;
            return true;
        }

        if (authorization == null)
        {
            errorResult = new UnauthorizedResult();
            return false;
        }

        if (!AuthorizationUtility.TryParseBasicAuthentication(
                authorization,
                out errorResult,
                out var userName,
                out var password))
        {
            return false;
        }

        if (!forkConfig.PrivateUsers.TryGetValue(userName, out var expectedPassword))
        {
            errorResult = new UnauthorizedResult();
            return false;
        }

        if (!AuthorizationUtility.BasicAuthMatches(password, expectedPassword))
        {
            errorResult = new UnauthorizedResult();
            return false;
        }

        errorResult = null;
        return true;
    }
}
