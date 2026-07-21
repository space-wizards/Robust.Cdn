using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Headers;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Integration tests for <c>DownloadCompatibilityController</c> (legacy /version/{version}/... routes).
///
/// Endpoints:
///   GET     /version/{version}/manifest
///   OPTIONS /version/{version}/download
///   POST    /version/{version}/download
/// </summary>
public sealed class DownloadCompatibilityControllerTests(
    WebApplicationFactory<Program> factory,
    DatabaseFixture database, 
    ITestOutputHelper testOutput
) : DownloadControllerTestBase(factory, database, testOutput)
{
    private const string CompatFork = "testfork-compat";

    protected override string ForkName => CompatFork;
    protected override string RoutePrefix => "/version";

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        return new()
        {
            ["Cdn:DefaultFork"] = CompatFork,
            ["Cdn:DatabaseFileName"] = Database.CreateTempDb(),
            ["Manifest:FileDiskPath"] = Database.CreateTestVersionOnDisk(CompatFork, ExistingVersion, content: FileContent),
        };
    }

    [Fact]
    public async Task GetManifest_VersionNotFound_ReturnsNotFound()
    {
        var client = Factory.CreateClient();

        var response = await PollUntilCondition(
            () => client.GetAsync("/version/0.0.0/manifest"),
            r => Task.FromResult(r.StatusCode == HttpStatusCode.NotFound));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetManifest_DefaultForkNull_ReturnsNotFound()
    {
        var client = FactoryWithNoDefaultFork().CreateClient();
        var response = await client.GetAsync($"/version/{ExistingVersion}/manifest");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DownloadOptions_DefaultForkNull_ReturnsNotFound()
    {
        var client = FactoryWithNoDefaultFork().CreateClient();
        var response = await client.SendAsync(new(HttpMethod.Options, $"/version/{ExistingVersion}/download"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DownloadPost_DefaultForkNull_ReturnsNotFound()
    {
        var client = FactoryWithNoDefaultFork().CreateClient();

        var content = new ByteArrayContent(new byte[4]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/version/{ExistingVersion}/download")
        {
            Content = content
        };
        request.Headers.Add("X-Robust-Download-Protocol", "1");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private WebApplicationFactory<Program> FactoryWithNoDefaultFork()
    {
        return Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Cdn:DefaultFork"] = null,
                });
            });
        });
    }
}
