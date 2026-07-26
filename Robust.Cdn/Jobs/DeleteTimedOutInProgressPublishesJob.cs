using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.DataAccessLayer;
using Robust.Cdn.Services;

namespace Robust.Cdn.Jobs;

/// <summary>
/// Job that periodically goes through and deletes old in-progress publishes that have "timed out".
/// </summary>
/// <remarks>
/// This deletes old in-progress publishes that have taken too long since being initiated,
/// which likely indicates that the publish encountered an error and will never be completed.
/// </remarks>
/// <seealso cref="ManifestOptions.InProgressPublishTimeoutMinutes"/>
public sealed class DeleteInProgressPublishesJob(
    PublishManager publishManager,
    ManifestDatabase manifestDatabase,
    TimeProvider timeProvider,
    IOptions<ManifestOptions> options,
    ILogger<DeleteInProgressPublishesJob> logger
) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var opts = options.Value;

        logger.LogTrace("Checking for timed out in-progress publishes");

        manifestDatabase.StartTransaction();

        var deleteBefore = timeProvider.GetUtcNow() - TimeSpan.FromMinutes(opts.InProgressPublishTimeoutMinutes);

        var totalDeleted = 0;

        var publishesInProgress = manifestDatabase.ListPublishInProgress();
        foreach (var (_, name, forkName, startTime) in publishesInProgress)
        {
            if (startTime >= deleteBefore)
                continue;

            logger.LogInformation("Deleting timed out publish for fork {Fork} version {Version}", forkName, name);

            publishManager.AbortMultiPublish(forkName, name);

            totalDeleted += 1;
        }

        manifestDatabase.Commit();

        logger.LogInformation("Deleted {TotalDeleted} timed out publishes", totalDeleted);

        return Task.CompletedTask;
    }
}
