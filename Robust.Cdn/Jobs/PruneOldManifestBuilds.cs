using Dapper;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;

namespace Robust.Cdn.Jobs;

/// <summary>
/// Job that periodically goes through and deletes old manifest builds.
/// </summary>
/// <remarks>
/// This job gets ran every 24 hours automatically.
/// </remarks>
/// <seealso cref="ManifestForkOptions.PruneBuildsDays"/>
public sealed class PruneOldManifestBuilds(
    ManifestDatabase manifestDatabase,
    IOptions<ManifestOptions> options,
    BuildDirectoryManager buildDirectoryManager,
    ISchedulerFactory schedulerFactory,
    ILogger<PruneOldManifestBuilds> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var opts = options.Value;

        logger.LogInformation("Pruning old manifest builds");

        var totalPruned = 0;
        var scheduler = await schedulerFactory.GetScheduler();

        foreach (var (forkName, forkConfig) in opts.Forks)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var forkPruned = PruneFork(forkName, forkConfig, context.CancellationToken);
            totalPruned += forkPruned;

            if (forkPruned > 0)
            {
                await scheduler.TriggerJob(
                    UpdateForkManifestJob.Key,
                    UpdateForkManifestJob.Data(forkName));
            }
        }

        logger.LogInformation("Pruned {Pruned} old manifest builds", totalPruned);
    }

    private int PruneFork(string forkName, ManifestForkOptions forkConfig, CancellationToken cancel)
    {
        if (forkConfig.PruneBuildsDays == 0)
        {
            logger.LogDebug("Not pruning fork {Fork}: pruning is disabled", forkConfig.PruneBuildsDays);
            return 0;
        }

        logger.LogDebug("Pruning old manifest builds for fork {Fork}", forkName);

        var pruneFrom = DateTime.UtcNow - TimeSpan.FromDays(forkConfig.PruneBuildsDays);

        var builds = manifestDatabase.Connection.Query<VersionData>("""
            SELECT FV.Id, FV.Name
            FROM ForkVersion FV, Fork
            WHERE FV.ForkId = Fork.Id
              AND Fork.Name = @ForkName
              AND FV.PublishedTime < @PruneFrom
            """, new { ForkName = forkName, PruneFrom = pruneFrom });

        var total = 0;
        foreach (var versionData in builds)
        {
            cancel.ThrowIfCancellationRequested();
            logger.LogDebug("Pruning fork version {Version}", versionData.Name);

            var directory = buildDirectoryManager.GetBuildVersionPath(forkName, versionData.Name);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                logger.LogTrace("Version directory deleted: {Directory}", directory);
            }
            else
            {
                logger.LogTrace("Version directory didn't exist when cleaning it up ({Directory})", directory);
            }

            manifestDatabase.Connection.Execute("DELETE FROM ForkVersion WHERE Id = @Id", versionData);
            total += 1;
        }

        return total;
    }

    private sealed class VersionData
    {
        public required int Id { get; set; }
        public required string Name { get; set; }
    }
}
