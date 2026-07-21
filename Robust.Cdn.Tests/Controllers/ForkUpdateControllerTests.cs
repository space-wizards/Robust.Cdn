using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Integration tests for <c>UpdateController.PostControlUpdate</c>.
///
/// Endpoint: POST /fork/{fork}/control/update
/// </summary>
[Trait("Category", "IntegrationTest")]
public sealed class ForkUpdateControllerTests(
    WebApplicationFactory<Program> factory,
    DatabaseFixture database,
    ITestOutputHelper testOutput)
    : TestBase(factory, database, testOutput)
{
    private const string UpdateFork = "testfork-update";
    private const string Token = "s3cret";

    protected override string ForkName => UpdateFork;

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        return new()
        {
            [$"Manifest:Forks:{UpdateFork}:UpdateToken"] = Token,
            ["Manifest:FileDiskPath"] = Database.CreateTestVersionOnDisk(UpdateFork, "1.0.0"),
            ["BaseUrl"] = "http://localhost/",
        };
    }

    [Fact]
    public async Task PostControlUpdate_NoAuthHeader_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var response = await client.PostAsync($"/fork/{UpdateFork}/control/update", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostControlUpdate_WrongToken_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{UpdateFork}/control/update");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostControlUpdate_ForkNotConfigured_ReturnsNotFound()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/fork/nonexistent/control/update");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Sadly it is too much of pain to actually test if job was triggered and completed properly.
    // Much easier to just test job logic separately.
    [Fact]
    public async Task PostControlUpdate_Success_ReturnsAccepted()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{UpdateFork}/control/update");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
