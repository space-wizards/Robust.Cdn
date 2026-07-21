using Microsoft.AspNetCore.Mvc.Testing;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Integration tests for <c>ForkPublishController.PostPublish</c> (one-shot publish).
///
/// Endpoint: POST /fork/{fork}/publish
/// </summary>
[Trait("Category", "IntegrationTest")]
public sealed class ForkPublishControllerOneShotTests(
    WebApplicationFactory<Program> factory,
    DatabaseFixture database, 
    ITestOutputHelper testOutput
) : TestBase(factory, database, testOutput)
{
    private const string PublishFork = "testfork-publish";
    private const string Token = "s3cret";

    protected override string ForkName => PublishFork;

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        return new()
        {
            [$"Manifest:Forks:{PublishFork}:UpdateToken"] = Token,
            ["Manifest:FileDiskPath"] = Database.CreateTestVersionOnDisk(PublishFork, "1.0.0"),
            ["BaseUrl"] = "http://localhost/",
        };
    }

    #region Auth

    [Fact]
    public async Task PostPublish_NoAuthHeader_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var content = PublishRequestContent();
        var response = await client.PostAsync($"/fork/{PublishFork}/publish", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostPublish_WrongAuthType_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{PublishFork}/publish")
        {
            Content = PublishRequestContent()
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "token");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostPublish_WrongToken_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{PublishFork}/publish")
        {
            Content = PublishRequestContent()
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region Validation — Version

    [Fact]
    public async Task PostPublish_EmptyVersion_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{PublishFork}/publish")
        {
            Content = PublishRequestContent(version: "")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostPublish_VersionAlreadyExists_ReturnsConflict()
    {
        var client = Factory.CreateClient();

        // Publish a build to create the version
        const string version = "2.0.0";
        var archivePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        CreatePublishArchive(archivePath, clientZipName: "test", content: "hello");
        await PublishOneShotRelease(client, archivePath, Token, version: version, basePort: 18990);

        // Try to publish the same version again
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{PublishFork}/publish")
        {
            Content = PublishRequestContent(version: version)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    #endregion

    #region Validation — Archive

    [Fact]
    public async Task PostPublish_EmptyArchiveUrl_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{PublishFork}/publish")
        {
            Content = PublishRequestContent(archive: string.Empty)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostPublish_UnreachableArchiveUrl_ReturnsInternalServerError()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{PublishFork}/publish")
        {
            Content = PublishRequestContent(archive: "http://127.0.0.1:18998/nonexistent.zip")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        // The HTTP client can't reach the URL, resulting in an exception → 500
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task PostPublish_MissingClientZip_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();

        var archivePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        CreateServerOnlyArchive(archivePath);

        const string version = "2.0.1";
        await using var host = new FileProvidingTemporaryHost(18997, archivePath);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{PublishFork}/publish")
        {
            Content = PublishRequestContent(
                version: version,
                archive: "http://127.0.0.1:18997/archive.zip")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Client zip is missing", body);
    }

    #endregion

    #region Fork not configured

    [Fact]
    public async Task PostPublish_ForkNotConfigured_ReturnsNotFound()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/fork/nonexistent/publish")
        {
            Content = PublishRequestContent()
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

            #endregion

    #region Success

    [Fact]
    public async Task PostPublish_Success_ReturnsNoContent()
    {
        var client = Factory.CreateClient();

        var archivePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        CreatePublishArchive(archivePath, clientZipName: "test", content: "hello from publish");

        const string version = "3.0.0";
        await PublishOneShotRelease(client, archivePath, Token, version: version, basePort: 18995);

        // Verify the version is accessible via GetFile
        var getResponse = await client.GetAsync($"/fork/{PublishFork}/version/{version}/file/test.zip");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal("application/zip", getResponse.Content.Headers.ContentType?.MediaType);

        // Verify the zip content is correct
        var data = await getResponse.Content.ReadAsByteArrayAsync();
        using var zipStream = new MemoryStream(data);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("data.txt");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        var actualContent = await reader.ReadToEndAsync();
        Assert.Equal("hello from publish", actualContent);
    }

    [Fact]
    public async Task PostPublish_Success_TriggersIngestAndPopulatesManifest()
    {
        var client = Factory.CreateClient();

        var archivePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        CreatePublishArchive(archivePath, clientZipName: "test", content: "from success test");

        const string version = "4.0.0";
        await PublishOneShotRelease(client, archivePath, Token, version: version, basePort: 18994);

        // Poll until manifest cache appears for that version
        var manifestResponse = await PollUntilCondition(
            () => client.GetAsync($"/fork/{PublishFork}/manifest"),
            async r => r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains(version));

        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        Assert.Equal("application/json", manifestResponse.Content.Headers.ContentType?.MediaType);
    }

    #endregion

    #region Helpers

    private static JsonContent PublishRequestContent(string? version = null, string? engineVersion = null, string? archive = null)
    {
        return JsonContent.Create(new
        {
            Version = version ?? "2.0.0",
            EngineVersion = engineVersion ?? "0.1.2",
            Archive = archive ?? "http://127.0.0.1:18999/archive.zip"
        });
    }

    #endregion
}
