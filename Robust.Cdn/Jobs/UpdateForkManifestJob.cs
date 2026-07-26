using Quartz;
using Robust.Cdn.DataAccessLayer;
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

        var forkId = database.GetForkIdByName(fork);

        logger.LogInformation("Updating manifest cache for fork {Fork}", fork);

        database.UpdateManifestCache(fork, forkId, baseUrlManager);

        if (notifyUpdate)
            await QueueNotifyWatchdogUpdate(fork);
    }

    private async Task QueueNotifyWatchdogUpdate(string fork)
    {
        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(
            NotifyWatchdogUpdateJob.Key,
            NotifyWatchdogUpdateJob.Data(fork)
        );
    }

}
