using System.Text.Json;
using Dapper;
using Quartz;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Jobs;

public sealed class MakeNewManifestVersionsAvailableJob(
    ManifestDatabase database,
    BaseUrlManager baseUrlManager,
    ILogger<MakeNewManifestVersionsAvailableJob> logger) : IJob
{
    private static readonly JsonSerializerOptions ManifestCacheContext = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static readonly JobKey Key = new(nameof(MakeNewManifestVersionsAvailableJob));

    public const string KeyForkName = "ForkName";
    public const string KeyVersions = "Versions";

    public static JobDataMap Data(string fork, IEnumerable<string> versions) => new()
    {
        { KeyForkName, fork },
        { KeyVersions, versions.ToArray() },
    };

    public Task Execute(IJobExecutionContext context)
    {
        var fork = context.MergedJobDataMap.GetString(KeyForkName) ?? throw new InvalidDataException();
        var versions = (string[])context.MergedJobDataMap.Get(KeyVersions) ?? throw new InvalidDataException();

        logger.LogInformation(
            "Updating version availability for manifest fork {Fork}, {VersionCount} new versions",
            fork,
            versions.Length);

        using var tx = database.Connection.BeginTransaction();

        var forkId = database.Connection.QuerySingle<int>(
            "SELECT Id FROM Fork WHERE Name = @ForkName",
            new { ForkName = fork });

        MakeVersionsAvailable(forkId, versions);

        tx.Commit();

        logger.LogInformation("Updating manifest cache");

        UpdateServerManifestCache(fork, forkId);

        return Task.CompletedTask;
    }

    private void MakeVersionsAvailable(int forkId, IEnumerable<string> versions)
    {
        foreach (var version in versions)
        {
            logger.LogInformation("New available version: {Version}", version);

            database.Connection.Execute("""
                UPDATE ForkVersion
                SET Available = TRUE
                WHERE Name = @Name
                  AND ForkId = @ForkId
                """,
                new
                {
                    Name = version,
                    ForkId = forkId
                });
        }
    }

    private void UpdateServerManifestCache(string fork, int forkId)
    {
        var data = CollectManifestData(fork, forkId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, ManifestCacheContext);

        database.Connection.Execute("UPDATE Fork SET ServerManifestCache = @Data WHERE Id = @ForkId",
            new
            {
                Data = bytes,
                ForkId = forkId
            });
    }

    private ManifestData CollectManifestData(string fork, int forkId)
    {
        var data = new ManifestData { Builds = new Dictionary<string, ManifestBuildData>() };

        var versions = database.Connection
            .Query<(int id, string name, DateTime time, string clientFileName, byte[] clientSha256)>(
                """
                SELECT Id, Name, PublishedTime, ClientFileName, ClientSha256
                FROM ForkVersion
                WHERE Available AND ForkId = @ForkId
                """,
                new { ForkId = forkId });

        foreach (var version in versions)
        {
            var buildData = new ManifestBuildData
            {
                Time = DateTime.SpecifyKind(version.time, DateTimeKind.Utc),
                Client = new ManifestArtifact
                {
                    Url = baseUrlManager.MakeBuildInfoUrl(
                        $"fork/{fork}/version/{version.name}/file/{version.clientFileName}"),
                    Sha256 = Convert.ToHexString(version.clientSha256)
                },
                Server = new Dictionary<string, ManifestArtifact>()
            };

            var servers = database.Connection.Query<(string platform, string fileName, byte[] sha256)>("""
                SELECT Platform, FileName, Sha256
                FROM ForkVersionServerBuild
                WHERE ForkVersionId = @ForkVersionId
                """, new { ForkVersionId = version.id });

            foreach (var (platform, fileName, sha256) in servers)
            {
                buildData.Server.Add(platform, new ManifestArtifact
                {
                    Url = baseUrlManager.MakeBuildInfoUrl($"fork/{fork}/version/{version.name}/file/{fileName}"),
                    Sha256 = Convert.ToHexString(sha256)
                });
            }

            data.Builds.Add(version.name, buildData);
        }

        return data;
    }

    private sealed class ManifestData
    {
        public required Dictionary<string, ManifestBuildData> Builds { get; set; }
    }

    private sealed class ManifestBuildData
    {
        public DateTime Time { get; set; }
        public required ManifestArtifact Client { get; set; }
        public required Dictionary<string, ManifestArtifact> Server { get; set; }
    }

    private sealed class ManifestArtifact
    {
        public required string Url { get; set; }
        public required string Sha256 { get; set; }
    }
}
