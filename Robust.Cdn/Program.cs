using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Robust.Cdn;
using Robust.Cdn.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();

// Add services to the container.

builder.Services.Configure<CdnOptions>(builder.Configuration.GetSection(CdnOptions.Position));

builder.Services.AddControllers();
builder.Services.AddSingleton<DataLoader>();
builder.Services.AddHostedService(services => services.GetRequiredService<DataLoader>());
builder.Services.AddSingleton<DownloadRequestLogger>();
builder.Services.AddHostedService(services => services.GetRequiredService<DownloadRequestLogger>());
builder.Services.AddTransient<Database>();

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
    var options = services.GetRequiredService<IOptions<CdnOptions>>().Value;
    var db = services.GetRequiredService<Database>().Connection;

    if (string.IsNullOrEmpty(options.VersionDiskPath))
    {
        loggerStartup.LogCritical("version disk path not set in configuration!");
        return;
    }

    db.Open();

    db.Execute("PRAGMA journal_mode=WAL");

    loggerStartup.LogDebug("Running migrations!");
    var loggerMigrator = logFactory.CreateLogger<Migrator>();

    Migrator.Migrate(loggerMigrator, db, "Robust.Cdn.Migrations");
    loggerStartup.LogDebug("Done running migrations!");

    Migrator.Migrate(loggerMigrator, db, "Robust.Cdn.Migrations");
    loggerStartup.LogDebug("Done running migrations!");

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

app.Run();
