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

builder.Services.Configure<CdnOptions>(builder.Configuration.GetSection(CdnOptions.Position));
builder.Services.Configure<ManifestOptions>(builder.Configuration.GetSection(ManifestOptions.Position));

builder.Services.AddControllers();
builder.Services.AddSingleton<DownloadRequestLogger>();
builder.Services.AddHostedService(services => services.GetRequiredService<DownloadRequestLogger>());
builder.Services.AddTransient<Database>();
builder.Services.AddTransient<ManifestDatabase>();
builder.Services.AddQuartz(q =>
{
    q.AddJob<IngestNewCdnContentJob>(j => j.WithIdentity(IngestNewCdnContentJob.Key).StoreDurably());
    q.AddJob<MakeNewManifestVersionsAvailableJob>(j =>
    {
        j.WithIdentity(MakeNewManifestVersionsAvailableJob.Key).StoreDurably();
    });
    q.AddJob<NotifyWatchdogUpdateJob>(j => j.WithIdentity(NotifyWatchdogUpdateJob.Key).StoreDurably());
});

builder.Services.AddQuartzHostedService(q =>
{
    q.WaitForJobsToComplete = true;
});

builder.Services.AddHttpClient(ForkPublishController.PublishFetchHttpClient);
builder.Services.AddHttpClient(NotifyWatchdogUpdateJob.HttpClientName);

builder.Services.AddScoped<BaseUrlManager>();
builder.Services.AddScoped<ForkAuthHelper>();
builder.Services.AddHttpContextAccessor();

/*
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
*/

var app = builder.Build();

// Make sure SQLite cleanly shuts down.
app.Lifetime.ApplicationStopped.Register(SqliteConnection.ClearAllPools);

{
    using var initScope = app.Services.CreateScope();
    var services = initScope.ServiceProvider;
    var logFactory = services.GetRequiredService<ILoggerFactory>();
    var loggerStartup = logFactory.CreateLogger("Robust.Cdn.Program");
    var manifestOptions = services.GetRequiredService<IOptions<ManifestOptions>>().Value;
    var db = services.GetRequiredService<Database>();
    var manifestDb = services.GetRequiredService<ManifestDatabase>();

    if (string.IsNullOrEmpty(manifestOptions.FileDiskPath))
    {
        loggerStartup.LogCritical("Manifest.FileDiskPath not set in configuration!");
        return 1;
    }

    if (manifestOptions.Forks.Count == 0)
    {
        loggerStartup.LogCritical("No forks defined in Manifest configuration!");
        return 1;
    }

    loggerStartup.LogDebug("Running migrations!");
    var loggerMigrator = logFactory.CreateLogger<Migrator>();

    var success = Migrator.Migrate(services, loggerMigrator, db.Connection, "Robust.Cdn.Migrations");
    success &= Migrator.Migrate(services, loggerMigrator, manifestDb.Connection, "Robust.Cdn.ManifestMigrations");
    if (!success)
        return 1;

    loggerStartup.LogDebug("Done running migrations!");

    loggerStartup.LogDebug("Ensuring forks created in manifest DB");
    manifestDb.EnsureForksCreated();
    loggerStartup.LogDebug("Done creating forks in manifest DB!");

    var scheduler = await initScope.ServiceProvider.GetRequiredService<ISchedulerFactory>().GetScheduler();
    foreach (var fork in manifestOptions.Forks.Keys)
    {
        await scheduler.TriggerJob(IngestNewCdnContentJob.Key, IngestNewCdnContentJob.Data(fork));
    }
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
