using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Integration tests for <c>ForkBuildPageController.Index</c>.
///
/// Endpoint: GET /fork/{fork}
/// </summary>
[Trait("Category", "IntegrationTest")]
public sealed class ForkBuildPageControllerTests(
    WebApplicationFactory<Program> factory,
    DatabaseFixture database,
    ITestOutputHelper testOutput)
    : TestBase(factory, database, testOutput)
{
    private readonly string? _buildsPageLink = "https://example-ss14-build.com";
    private const string PublicFork = "testfork-buildpage-public";
    private const string PrivateFork = "testfork-buildpage-private";
    private const string Token = "s3cret";
    private const string ValidUser = "admin";
    private const string ValidPassword = "hunter2";

    protected override string ForkName => PublicFork;

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        return new()
        {
            [$"Manifest:Forks:{PublicFork}:DisplayName"] = "Public Builds",
            [$"Manifest:Forks:{PublicFork}:BuildsPageLink"] = _buildsPageLink,
            [$"Manifest:Forks:{PublicFork}:BuildsPageLinkText"] = "example.com",
            [$"Manifest:Forks:{PublicFork}:UpdateToken"] = Token,
            [$"Manifest:Forks:{PrivateFork}:Private"] = "true",
            [$"Manifest:Forks:{PrivateFork}:PrivateUsers:{ValidUser}"] = ValidPassword,
            [$"Manifest:Forks:{PrivateFork}:UpdateToken"] = Token,
            ["Manifest:FileDiskPath"] = Database.CreateTestVersionOnDisk(PublicFork, "0.0.1"),
            ["BaseUrl"] = "http://localhost/",
        };
    }

    [Fact]
    public async Task Index_ForkNotConfigured_ReturnsNotFound()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync("/fork/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Index_PrivateForkNoAuth_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync($"/fork/{PrivateFork}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Index_PrivateForkWrongPassword_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/fork/{PrivateFork}");
        request.Headers.Authorization = BasicAuthHeader(ValidUser, "wrongpassword");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Index_PrivateForkValidAuth_ReturnsHtml()
    {
        var client = Factory.CreateClient();

        // Publish a build first so there's data to show
        await SetupPublishedBuild(client, "2.0.0");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/fork/{PrivateFork}");
        request.Headers.Authorization = BasicAuthHeader(ValidUser, ValidPassword);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("testfork-buildpage-private builds", body);
    }

    [Fact]
    public async Task Index_PublicFork_ReturnsHtml()
    {
        var client = Factory.CreateClient();

        // Publish a build first so there's data to show
        await SetupPublishedBuild(client, "2.0.0");

        var response = await client.GetAsync($"/fork/{PublicFork}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Public Builds builds</h1>", body);
    }

    [Fact]
    public async Task Index_ZeroBuilds_ShowsNoBuildsYetMessage()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync($"/fork/{PublicFork}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("There are no server builds yet", body);
    }

    [Fact]
    public async Task Index_OneBuild_ShowsSingleBuild()
    {
        var client = Factory.CreateClient();
        await SetupPublishedBuild(client, "3.0.0");

        var response = await PollUntilCondition(
            () => client.GetAsync($"/fork/{PublicFork}"),
            async r =>
            {
                if (r.StatusCode != HttpStatusCode.OK)
                    return false;
                var body = await r.Content.ReadAsStringAsync();
                return body.Contains("Latest build");
            });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Latest build", body);
        Assert.Contains("3.0.0", body);
        Assert.Matches(@"<h2>Old builds<\/h2>\s*<\/main>", body);
    }

    [Fact]
    public async Task Index_MultipleBuilds_ShowsLatestAndOldBuildsSections()
    {
        var client = Factory.CreateClient();

        // Publish 2+ builds
        await SetupPublishedBuild(client, "4.0.0", basePort: 18770);
        await SetupPublishedBuild(client, "4.1.0", basePort: 18771);

        var response = await PollUntilCondition(
            () => client.GetAsync($"/fork/{PublicFork}"),
            async r =>
            {
                if (r.StatusCode != HttpStatusCode.OK)
                    return false;
                var body = await r.Content.ReadAsStringAsync();
                return body.Contains("4.1.0");
            });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Latest build", body);
        Assert.Matches(@"<h2>Latest build<\/h2>[\s\S]*?<span class=""versionNumber"">4.1.0<\/span>[\s\S]*?<h2>Old builds<\/h2>", body);
        Assert.Contains("Old builds", body);
        Assert.Matches(@"<h2>Old builds<\/h2>[\s\S]*?<span class=""versionNumber"">4.0.0<\/span>", body);
    }

    [Fact]
    public async Task Index_HasBuildsPageLink_ShowsLinkWithConfiguredText()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync($"/fork/{PublicFork}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"href=\"{_buildsPageLink}\"", body);
    }

    #region Helpers

    private async Task SetupPublishedBuild(HttpClient client, string? version = null, int basePort = 18765)
    {
        var archivePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        CreatePublishArchive(archivePath, clientZipName: "test", content: "hello from build page");
        await PublishOneShotRelease(client, archivePath, Token, version: version, basePort: basePort);
    }

    private static AuthenticationHeaderValue BasicAuthHeader(string user, string password)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    #endregion
}
