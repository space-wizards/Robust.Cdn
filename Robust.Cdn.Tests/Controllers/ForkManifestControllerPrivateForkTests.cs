using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

[Trait("Category", "IntegrationTest")]
public sealed class ForkManifestControllerPrivateForkTests(
    WebApplicationFactory<Program> factory,
    DatabaseFixture database, 
    ITestOutputHelper testOutput
) : TestBase(factory, database, testOutput)
{
    private const string PrivateForkName = "testfork-private";
    private const string Token = "s3cret";
    private const string ValidUser = "admin";
    private const string ValidPassword = "hunter2";

    protected override string ForkName => PrivateForkName;

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        return new()
        {
            [$"Manifest:Forks:{PrivateForkName}:Private"] = "true",
            [$"Manifest:Forks:{PrivateForkName}:UpdateToken"] = Token,
            [$"Manifest:Forks:{PrivateForkName}:PrivateUsers:{ValidUser}"] = ValidPassword,
            ["Manifest:FileDiskPath"] = Database.CreateTestVersionOnDisk(PrivateForkName, "1.0.0"),
            ["BaseUrl"] = "http://localhost/",
        };
    }

    #region Get Manifest — Auth

    [Fact]
    public async Task GetManifest_PrivateForkNoAuth_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync($"/fork/{PrivateForkName}/manifest");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task GetManifest_PrivateForkWrongPassword_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/fork/{PrivateForkName}/manifest");
        request.Headers.Authorization = BasicAuthHeader(ValidUser, "wrongpassword");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetManifest_PrivateForkUnknownUser_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/fork/{PrivateForkName}/manifest");
        request.Headers.Authorization = BasicAuthHeader("unknown", ValidPassword);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetManifest_PrivateForkValidAuth_ReturnsJson()
    {
        var client = Factory.CreateClient();

        // Publish a build first so there's manifest cache to read.
        const string version = "2.0.0";
        await SetupPublishedBuild(client, version);

        var response = await PollUntilCondition(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"/fork/{PrivateForkName}/manifest");
                req.Headers.Authorization = BasicAuthHeader(ValidUser, ValidPassword);
                return client.SendAsync(req);
            },
            async r => r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains(version));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }


    #endregion

    #region Get File — Auth

    [Fact]
    public async Task GetFile_PrivateForkNoAuth_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync($"/fork/{PrivateForkName}/version/1.0.0/file/test.zip");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task GetFile_PrivateForkWrongPassword_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/fork/{PrivateForkName}/version/1.0.0/file/test.zip");
        request.Headers.Authorization = BasicAuthHeader(ValidUser, "wrongpassword");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetFile_PrivateForkValidAuth_ReturnsZip()
    {
        var client = Factory.CreateClient();

        const string version = "2.0.1";
        await SetupPublishedBuild(client, version);

        var response = await PollUntilCondition(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"/fork/{PrivateForkName}/version/{version}/file/test.zip");
                req.Headers.Authorization = BasicAuthHeader(ValidUser, ValidPassword);
                return client.SendAsync(req);
            },
            r => Task.FromResult(r.StatusCode == HttpStatusCode.OK));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
    }

    #endregion

    #region Helpers

    private async Task SetupPublishedBuild(HttpClient client, string? version = null)
    {
        var archivePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        CreatePublishArchive(archivePath, clientZipName: "test", content: "hello from publish");
        await PublishOneShotRelease(client, archivePath, Token, version: version, basePort: 18766);
    }

    private static AuthenticationHeaderValue BasicAuthHeader(string user, string password)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    #endregion
}
