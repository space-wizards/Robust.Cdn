using System.Text.Json;
using Quartz;
using Robust.Cdn.DataAccessLayer;

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

        database.StartTransaction();
        database.SetForkVersionsAvailable(fork, versions);
        database.Commit();

        logger.LogInformation("New available versions: {Version}", string.Join(" , ", versions));

        var scheduler = await factory.GetScheduler();
        await scheduler.TriggerJob(
            UpdateForkManifestJob.Key,
            UpdateForkManifestJob.Data(fork, notifyUpdate: true));
    }

}
