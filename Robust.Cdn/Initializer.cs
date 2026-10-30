using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.Jobs;

namespace Robust.Cdn;

public static class Initializer
{
    public static async Task<bool> TryInit(IServiceProvider sp)
    {
        using var initScope = sp.CreateScope();
        var services = initScope.ServiceProvider;
        var logFactory = services.GetRequiredService<ILoggerFactory>();
        var loggerStartup = logFactory.CreateLogger("Robust.Cdn.Program");
        var manifestOptions = services.GetRequiredService<IOptions<ManifestOptions>>().Value;
        var db = services.GetRequiredService<Database>();
        var manifestDb = services.GetRequiredService<ManifestDatabase>();

        if (string.IsNullOrEmpty(manifestOptions.FileDiskPath))
        {
            loggerStartup.LogCritical("Manifest.FileDiskPath not set in configuration!");
            return false;
        }

        if (manifestOptions.Forks.Count == 0)
        {
            loggerStartup.LogCritical("No forks defined in Manifest configuration!");
            return false;
        }

        loggerStartup.LogDebug("Running migrations!");
        var loggerMigrator = logFactory.CreateLogger<Migrator>();

        var success = Migrator.Migrate(services, loggerMigrator, db.Connection, "Robust.Cdn.Migrations");
        success &= Migrator.Migrate(services, loggerMigrator, manifestDb.Connection, "Robust.Cdn.ManifestMigrations");
        if (!success)
            return false;

        loggerStartup.LogDebug("Done running migrations!");

        loggerStartup.LogDebug("Ensuring forks created in manifest DB");
        manifestDb.EnsureForksCreated();
        loggerStartup.LogDebug("Done creating forks in manifest DB!");

        var scheduler = await initScope.ServiceProvider.GetRequiredService<ISchedulerFactory>().GetScheduler();
        foreach (var fork in manifestOptions.Forks.Keys)
        {
            await scheduler.TriggerJob(IngestNewCdnContentJob.Key, IngestNewCdnContentJob.Data(fork));
        }

        return true;
    }
}
