using Microsoft.Data.Sqlite;
using Robust.Cdn;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();

// Add services to the container.

builder.Services.SetupDependencies(builder.Configuration);

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

if (!await Initializer.TryInit(app.Services))
    return 1;

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

// required for visibility of entry-point to test framework
public partial class Program { }
