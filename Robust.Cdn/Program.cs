using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn;
using Robust.Cdn.Config;
using Robust.Cdn.Controllers;
using Robust.Cdn.Helpers;
using Robust.Cdn.Jobs;
using Robust.Cdn.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();

// Add services to the container.

SetupDependencies(builder.Services, builder.Configuration);

var app = builder.Build();

var pathBase = app.Configuration.GetValue<string>("PathBase");
if (!string.IsNullOrEmpty(pathBase))
{
    app.Services.GetRequiredService<ILogger<Program>>().LogInformation("Using PathBase: {PathBase}", pathBase);
    app.UsePathBase(pathBase);
}

app.UseRouting();

// Make sure SQLite cleanly shuts down.
app.Lifetime.ApplicationStopped.Register(SqliteConnection.ClearAllPools);

{
    if (!await TryInit(app.Services))
        return 1;
}
/*
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
app.UseSwagger();
app.UseSwaggerUI();
}
*/

// app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

await app.RunAsync();

return 0;

static async Task<bool> TryInit(IServiceProvider sp)
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

static void SetupDependencies(IServiceCollection services, IConfiguration configuration)
{
    services.Configure<CdnOptions>(configuration.GetSection(CdnOptions.Position));
    services.Configure<ManifestOptions>(configuration.GetSection(ManifestOptions.Position));

    services.AddControllersWithViews();
    services.AddScoped<BuildDirectoryManager>();
    services.AddSingleton<DownloadRequestLogger>();
    services.AddHostedService(s => s.GetRequiredService<DownloadRequestLogger>());
    services.AddScoped<Database>();
    services.AddScoped<ManifestDatabase>();
    services.AddScoped<PublishManager>();
    services.AddSingleton(TimeProvider.System);
    services.AddQuartz(q =>
    {
        q.AddJob<IngestNewCdnContentJob>(j => j.WithIdentity(IngestNewCdnContentJob.Key).StoreDurably());
        q.AddJob<MakeNewManifestVersionsAvailableJob>(j =>
        {
            j.WithIdentity(MakeNewManifestVersionsAvailableJob.Key).StoreDurably();
        });
        q.AddJob<NotifyWatchdogUpdateJob>(j => j.WithIdentity(NotifyWatchdogUpdateJob.Key).StoreDurably());
        q.AddJob<UpdateForkManifestJob>(j => j.WithIdentity(UpdateForkManifestJob.Key).StoreDurably());
        q.ScheduleJob<PruneOldManifestBuilds>(trigger => trigger.WithSimpleSchedule(schedule =>
        {
            schedule.RepeatForever().WithIntervalInHours(24);
        }));
        q.ScheduleJob<DeleteInProgressPublishesJob>(t =>
            t.WithSimpleSchedule(s => s.RepeatForever().WithIntervalInHours(24)));
    });

    services.AddQuartzHostedService(q =>
    {
        q.WaitForJobsToComplete = true;
    });

    const string userAgent = "Robust.Cdn";

    services.AddHttpClient(ForkPublishController.PublishFetchHttpClient, c =>
    {
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(userAgent, null));
    });
    services.AddHttpClient(NotifyWatchdogUpdateJob.HttpClientName, c =>
    {
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(userAgent, null));
    });

    services.AddScoped<BaseUrlManager>();
    services.AddScoped<ForkAuthHelper>();
    services.AddHttpContextAccessor();

    /*
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    */
}

// required for visibility of entry-point to test framework
public partial class Program { }
