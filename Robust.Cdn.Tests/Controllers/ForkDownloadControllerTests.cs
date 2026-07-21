using System.Buffers.Binary;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Headers;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Integration tests for <c>DownloadController</c> via the standard /fork/{fork}/version/{version}/... route.
///
/// Endpoints:
///   GET     /fork/{fork}/version/{version}/manifest
///   OPTIONS /fork/{fork}/version/{version}/download
///   POST    /fork/{fork}/version/{version}/download
/// </summary>
public sealed class ForkDownloadControllerTests(
    WebApplicationFactory<Program> factory,
    DatabaseFixture database,
    ITestOutputHelper testOutput
) : DownloadControllerTestBase(factory, database, testOutput)
{
    protected override string ForkName => "testfork2";

    protected override string RoutePrefix => $"/fork/{ForkName}/version";

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        return new()
        {
            ["Manifest:FileDiskPath"] = Database.CreateTestVersionOnDisk(ForkName, ExistingVersion, content: FileContent),
        };
    }

    [Theory]
    [InlineData("nonexistent", "1.0.0")]
    [InlineData("nonexistent", "0.0.0")]
    [InlineData("testfork2", "0.0.0")]
    public async Task GetManifest_NonExistentForkOrVersion_ReturnsNotFound(string fork, string version)
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync($"/fork/{fork}/version/{version}/manifest");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DownloadPost_EmptyRequestBody_ReturnsStreamHeaderOnly()
    {
        var client = Factory.CreateClient();

        var response = await PollUntilOk(() =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{RoutePrefix}/{ExistingVersion}/download")
            {
                Content = content
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            return client.SendAsync(request);
        });

        var responseBody = await response.Content.ReadAsByteArrayAsync();
        // Just the 4-byte stream header, no files
        Assert.Equal(4, responseBody.Length);

        // Stream header flags = 1 (PreCompressed by default)
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(0, 4)));
    }
}

