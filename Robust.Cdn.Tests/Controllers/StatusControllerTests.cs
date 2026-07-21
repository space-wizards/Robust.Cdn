using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Integration tests for <c>StatusController</c>.
///
/// Endpoint: GET /control/status
/// </summary>
[Trait("Category", "Integration")]
public sealed class StatusControllerTests(
    WebApplicationFactory<Program> factory, 
    DatabaseFixture database, 
    ITestOutputHelper testOutput
) : TestBase(factory, database, testOutput)
{
    protected override string ForkName => "testfork";

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        var baseDir = Database.CreateTestVersionOnDisk(ForkName, "1.0.0");
        return new()
        {
            ["Manifest:FileDiskPath"] = baseDir,
        };
    }

    [Fact]
    public async Task GetControlStatus_ReturnsOkWithVersionInfo()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync("/control/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("OK", content);
        Assert.Contains("contentVersions", content);
        Assert.Contains("version", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetControlStatus_WithExistingVersion_ReturnsNonZeroCount()
    {
        var client = Factory.CreateClient();

        // The ingest job runs asynchronously at startup. Poll until we see version info.
        await PollUntilCondition(() => client.GetAsync("/control/status"), async response =>
        {
            var body = await response.Content.ReadAsStringAsync();
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
            return deserialized != null
                && deserialized.TryGetValue("contentVersions", out var raw)
                && int.TryParse(raw?.ToString(), out var count)
                && count > 0;
        });
    }
}

