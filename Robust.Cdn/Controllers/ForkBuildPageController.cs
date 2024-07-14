using System.Diagnostics.CodeAnalysis;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;

namespace Robust.Cdn.Controllers;

[Controller]
[Route("/fork/{fork}")]
public sealed class ForkBuildPageController(
    ManifestDatabase database,
    IOptions<ManifestOptions> manifestOptions)
    : Controller
{
    [HttpGet]
    public IActionResult Index(string fork)
    {
        if (!TryCheckBasicAuth(fork, out var errorResult))
            return errorResult;

        var versions = new List<Version>();

        using var tx = database.Connection.BeginTransaction();

        var dbVersions = database.Connection.Query<DbVersion>(
            """
            SELECT FV.Id, FV.Name, PublishedTime, EngineVersion
            FROM ForkVersion FV
            INNER JOIN main.Fork F ON FV.ForkId = F.Id
            WHERE F.Name = @Fork
              AND FV.Available
            ORDER BY PublishedTime DESC
            LIMIT 50
            """, new { Fork = fork });

        foreach (var dbVersion in dbVersions)
        {
            var servers = database.Connection.Query<VersionServer>("""
                SELECT Platform, FileName, FileSize
                FROM ForkVersionServerBuild
                WHERE ForkVersionId = @ForkVersionId
                ORDER BY Platform
                """, new { ForkVersionId = dbVersion.Id });

            versions.Add(new Version
            {
                Name = dbVersion.Name,
                EngineVersion = dbVersion.EngineVersion,
                PublishedTime = DateTime.SpecifyKind(dbVersion.PublishedTime, DateTimeKind.Utc),
                Servers = servers.ToArray()
            });
        }

        return View(new Model
        {
            Fork = fork,
            Options = manifestOptions.Value.Forks[fork],
            Versions = versions
        });
    }

    private bool TryCheckBasicAuth(
        string fork,
        [NotNullWhen(false)] out IActionResult? errorResult)
    {
        return ForkManifestController.TryCheckBasicAuth(HttpContext, manifestOptions.Value, fork, out errorResult);
    }

    public sealed class Model
    {
        public required string Fork;
        public required ManifestForkOptions Options;
        public required List<Version> Versions;
    }

    public sealed class Version
    {
        public required string Name;
        public required DateTime PublishedTime;
        public required string EngineVersion;
        public required VersionServer[] Servers;
    }

    public sealed class VersionServer
    {
        public required string Platform { get; set; }
        public required string FileName { get; set; }
        public required long? FileSize { get; set; }
    }

    private sealed class DbVersion
    {
        public required int Id { get; set; }
        public required string Name { get; set; }
        public required DateTime PublishedTime { get; set; }
        public required string EngineVersion { get; set; }
    }
}
