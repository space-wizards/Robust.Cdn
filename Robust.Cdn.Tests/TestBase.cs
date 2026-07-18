using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Robust.Cdn.Tests;

/// <summary>
/// Base class for integration tests providing a pre-configured <c>WebApplicationFactory</c>
/// and automatic cleanup of temp database files after the test collection finishes.
/// </summary>
[Collection("ControllerTests")]
public abstract class TestBase : IClassFixture<DatabaseFixture>
{
    protected WebApplicationFactory<Program> Factory { get; }
    protected DatabaseFixture Database { get; }

    protected abstract string ForkName { get; }

    protected TestBase(WebApplicationFactory<Program> factory, DatabaseFixture database)
    {
        Database = database;

        var config = new Dictionary<string, string?>(GetDefaultConfiguration());
        foreach (var (key, value) in GetConfigurationOverrides())
        {
            config[key] = value;
        }

        Factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(config);
            });
        });
    }

    /// <summary>
    /// Provides default configuration for all tests.
    /// Override to change defaults globally.
    /// </summary>
    private Dictionary<string, string?> GetDefaultConfiguration()
    {
        return new()
        {
            ["Cdn:DatabaseFileName"] = Database.CreateTempDb(),
            ["Manifest:DatabaseFileName"] = Database.CreateTempDb(),
            ["Manifest:FileDiskPath"] = Path.GetTempPath(),
            [$"Manifest:Forks:{ForkName}:ClientZipName"] = "test",
            [$"Manifest:Forks:{ForkName}:BuildsPageLinkText"] = "test",
        };
    }

    /// <summary>
    /// Override to add or replace specific configuration keys for a particular test class.
    /// Returned values take precedence over <c>GetDefaultConfiguration()</c>.
    /// Please do not use <see cref="Factory"/> there, as this method is used
    /// in c-tor or base class and factory won't be set up yet (config is required for factory).
    /// </summary>
    protected virtual Dictionary<string, string?> GetConfigurationOverrides() => new();

    /// <summary>
    /// Polls a request until it returns OK or the timeout is reached.
    /// Useful for waiting on async jobs triggered during startup (e.g. version ingestion).
    /// </summary>
    protected static async Task<HttpResponseMessage> PollUntilCondition(
        Func<Task<HttpResponseMessage>> factory,
        Func<HttpResponseMessage, Task<bool>> condition,
        int maxAttempts = 20,
        int delayMs = 200)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            var response = await factory();
            if (await condition(response))
                return response;

            await Task.Delay(delayMs);
        }

        throw new TimeoutException($"Request did not return OK after {maxAttempts * delayMs}ms");
    }

    /// <summary>
    /// Polls a GET request until it returns OK.
    /// </summary>
    protected static Task<HttpResponseMessage> PollUntilOk(Func<Task<HttpResponseMessage>> factory, int maxAttempts = 20, int delayMs = 200)
    {
        return PollUntilCondition(factory, response => Task.FromResult(response.StatusCode == HttpStatusCode.OK), maxAttempts, delayMs);
    }
}

