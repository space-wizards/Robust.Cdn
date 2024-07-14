using System.Text.Json;
using Dapper;
using Quartz;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Jobs;

public sealed class MakeNewManifestVersionsAvailableJob(
    ManifestDatabase database,
    ISchedulerFactory factory,
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

    public async Task Execute(IJobExecutionContext context)
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

        var scheduler = await factory.GetScheduler();
        await scheduler.TriggerJob(
            UpdateForkManifestJob.Key,
            UpdateForkManifestJob.Data(fork, notifyUpdate: true));
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
}
