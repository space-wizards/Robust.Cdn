using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Quartz;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Jobs;

/// <summary>
/// Updates the cached server manifest for a fork.
/// </summary>
public sealed class UpdateForkManifestJob(
    ManifestDatabase database,
    BaseUrlManager baseUrlManager,
    ISchedulerFactory schedulerFactory,
    ILogger<MakeNewManifestVersionsAvailableJob> logger) : IJob
{
    private static readonly JsonSerializerOptions ManifestCacheContext = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static readonly JobKey Key = new(nameof(UpdateForkManifestJob));

    public const string KeyForkName = "ForkName";
    public const string KeyNotifyUpdate = "NotifyUpdate";

    public static JobDataMap Data(string fork, bool notifyUpdate = false) => new()
    {
        { KeyForkName, fork },
        { KeyNotifyUpdate, notifyUpdate }
    };

    public async Task Execute(IJobExecutionContext context)
    {
        var fork = context.MergedJobDataMap.GetString(KeyForkName) ?? throw new InvalidDataException();
        var notifyUpdate = context.MergedJobDataMap.GetBooleanValue(KeyNotifyUpdate);

        var forkId = database.Connection.QuerySingle<int>(
            "SELECT Id FROM Fork WHERE Name = @ForkName",
            new { ForkName = fork });

        logger.LogInformation("Updating manifest cache for fork {Fork}", fork);

        UpdateServerManifestCache(fork, forkId);

        if (notifyUpdate)
            await QueueNotifyWatchdogUpdate(fork);
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

            var servers = database.Connection.Query<(string platform, string fileName, byte[] sha256, long? size)>("""
                SELECT Platform, FileName, Sha256, FileSize
                FROM ForkVersionServerBuild
                WHERE ForkVersionId = @ForkVersionId
                """, new { ForkVersionId = version.id });

            foreach (var (platform, fileName, sha256, size) in servers)
            {
                buildData.Server.Add(platform, new ManifestArtifact
                {
                    Url = baseUrlManager.MakeBuildInfoUrl($"fork/{fork}/version/{version.name}/file/{fileName}"),
                    Sha256 = Convert.ToHexString(sha256),
                    Size = size
                });
            }

            data.Builds.Add(version.name, buildData);
        }

        return data;
    }

    private async Task QueueNotifyWatchdogUpdate(string fork)
    {
        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(
            NotifyWatchdogUpdateJob.Key,
            NotifyWatchdogUpdateJob.Data(fork));
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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Size { get; set; }
    }
}
