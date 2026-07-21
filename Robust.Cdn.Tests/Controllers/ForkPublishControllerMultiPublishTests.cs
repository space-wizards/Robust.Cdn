using Microsoft.AspNetCore.Mvc.Testing;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Integration tests for the multi-request publish workflow:
///   POST /fork/{fork}/publish/start
///   POST /fork/{fork}/publish/file   (with headers Robust-Cdn-Publish-File, Robust-Cdn-Publish-Version)
///   POST /fork/{fork}/publish/finish
/// </summary>
[Trait("Category", "IntegrationTest")]
public sealed class ForkPublishControllerMultiPublishTests(
    WebApplicationFactory<Program> factory,
    DatabaseFixture database,
    ITestOutputHelper testOutput
) : TestBase(factory, database , testOutput)
{
    private const string MultiForkSuccesses= "testfork-multi-successes";
    private const string MultiFork = "testfork-multi-failures";
    private const string Token = "s3cret";

    protected override string ForkName => MultiFork;

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        return new()
        {
            [$"Manifest:Forks:{MultiForkSuccesses}:ClientZipName"] = "test",
            [$"Manifest:Forks:{MultiForkSuccesses}:BuildsPageLinkText"] = "test",
            [$"Manifest:Forks:{MultiForkSuccesses}:UpdateToken"] = Token,
            [$"Manifest:Forks:{MultiFork}:UpdateToken"] = Token,
            ["Manifest:FileDiskPath"] = Database.CreateTestVersionOnDisk(MultiFork, "1.0.0"),
            ["BaseUrl"] = "http://localhost/",
        };
    }

    #region Auth

    [Fact]
    public async Task MultiPublishStart_NoAuth_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var content = JsonContent.Create(new { Version = "2.0.0", EngineVersion = "0.1.2" });
        var response = await client.PostAsync($"/fork/{MultiFork}/publish/start", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MultiPublishStart_WrongToken_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/start")
        {
            Content = JsonContent.Create(new { Version = "2.0.0", EngineVersion = "0.1.2" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MultiPublishFile_NoAuth_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/file")
        {
            Content = content
        };
        request.Headers.Add("Robust-Cdn-Publish-File", "test.zip");
        request.Headers.Add("Robust-Cdn-Publish-Version", "2.0.0");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MultiPublishFinish_NoAuth_ReturnsUnauthorized()
    {
        var client = Factory.CreateClient();
        var content = JsonContent.Create(new { Version = "2.0.0" });
        var response = await client.PostAsync($"/fork/{MultiFork}/publish/finish", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region Start — Validation

    [Fact]
    public async Task MultiPublishStart_InvalidVersion_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/start")
        {
            Content = JsonContent.Create(new { Version = "", EngineVersion = "0.1.2" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MultiPublishStart_VersionAlreadyExists_ReturnsConflict()
    {
        var client = Factory.CreateClient();

        // First complete publish of version 2.0.0 via one-shot
        var archivePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        CreatePublishArchive(archivePath, clientZipName: "test", content: "hello");
        await PublishOneShotRelease(client, archivePath, Token, version: "2.0.0", basePort: 18980);

        // Try to start multi-publish for the same version
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/start")
        {
            Content = JsonContent.Create(new { Version = "2.0.0", EngineVersion = "0.1.2" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task MultiPublishStart_ForkNotConfigured_ReturnsNotFound()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/fork/nonexistent/publish/start")
        {
            Content = JsonContent.Create(new { Version = "2.0.0", EngineVersion = "0.1.2" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MultiPublishStart_VersionAlreadyInProgress_AbortsAndRestarts()
    {
        var client = Factory.CreateClient();

        const string version = "200.0.0";

        var start1Req = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/start")
        {
            Content = JsonContent.Create(new { Version = version, EngineVersion = "0.1.2" })
        };
        start1Req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var start1Resp = await client.SendAsync(start1Req);
        Assert.Equal(HttpStatusCode.NoContent, start1Resp.StatusCode);

        await UploadMultiPublishFile(client, version, "SS14.Server_linux-x64.zip", MultiFork, CreateServerZip("first attempt"));

        var start2Req = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/start")
        {
            Content = JsonContent.Create(new { Version = version, EngineVersion = "0.1.3" })
        };
        start2Req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var start2Resp = await client.SendAsync(start2Req);
        Assert.Equal(HttpStatusCode.NoContent, start2Resp.StatusCode);

        const string expected = "restarted";
        var clientZipData = CreateClientZip(expected);
        await UploadMultiPublishFile(client, version, "test.zip", MultiFork, clientZipData);
        await UploadMultiPublishFile(client, version, "SS14.Server_linux-x64.zip", MultiFork, CreateServerZip(expected));

        var finishRequest = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/finish")
        {
            Content = JsonContent.Create(new { Version = version })
        };
        finishRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var finishResponse = await client.SendAsync(finishRequest);
        Assert.Equal(HttpStatusCode.NoContent, finishResponse.StatusCode);

        await PollUntilCondition(
            () => client.GetAsync($"/fork/{MultiFork}/manifest"),
            async r => r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains(version),
            maxAttempts: 30
        );

        var getResponse = await client.GetAsync($"/fork/{MultiFork}/version/{version}/file/test.zip");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var actualContent = await ReadFileContent(getResponse);
        Assert.Equal(expected, actualContent);

    }

    [Fact]
    public async Task MultiPublishFile_NoVersionHeader_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/file")
        {
            Content = content
        };
        request.Headers.Add("Robust-Cdn-Publish-File", "test.zip");
        // Missing Robust-Cdn-Publish-Version header
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        // Without the version header, the parameter is null; model binding or validation produces BadRequest
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MultiPublishFile_NoInProgressPublish_ReturnsNotFound()
    {
        var client = Factory.CreateClient();

        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/file")
        {
            Content = content
        };
        request.Headers.Add("Robust-Cdn-Publish-File", "test.zip");
        request.Headers.Add("Robust-Cdn-Publish-Version", "999.0.0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MultiPublishFile_InvalidFileName_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();

                // Start a publish first so the DB lookup succeeds
        const string version = "103.0.0";
        var startRequest = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/start")
        {
            Content = JsonContent.Create(new { Version = version, EngineVersion = "0.1.2" })
        };
        startRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        await client.SendAsync(startRequest);

        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiFork}/publish/file")
        {
            Content = content
        };
        request.Headers.Add("Robust-Cdn-Publish-File", "..");
        request.Headers.Add("Robust-Cdn-Publish-Version", version);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        // ".." doesn't match ValidFileRegex (first char must be [a-zA-Z0-9\-_])
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Full Workflow

    [Fact]
    public async Task MultiPublish_FullSuccessFlow_CompletesPublish()
    {
        var client = Factory.CreateClient();

        const string version = "100.0.0";
        const string engineVersion = "0.1.2";

        // 1. Start
        var startRequest = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiForkSuccesses}/publish/start")
        {
            Content = JsonContent.Create(new { Version = version, EngineVersion = engineVersion })
        };
        startRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var startResponse = await client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.NoContent, startResponse.StatusCode);

        // 2. Upload client zip
        await UploadMultiPublishFile(client, version, "test.zip", MultiForkSuccesses, CreateClientZip("hello from multi-publish"));

        // 3. Upload server zips
        await UploadMultiPublishFile(client, version, "SS14.Server_linux-x64.zip", MultiForkSuccesses, CreateServerZip("linux"));
        await UploadMultiPublishFile(client, version, "SS14.Server_win-x64.zip", MultiForkSuccesses, CreateServerZip("win"));

        // 4. Finish
        var finishRequest = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiForkSuccesses}/publish/finish")
        {
            Content = JsonContent.Create(new { Version = version })
        };
        finishRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var finishResponse = await client.SendAsync(finishRequest);
        Assert.Equal(HttpStatusCode.NoContent, finishResponse.StatusCode);

        // 5. Verify — manifest cache should appear
        await PollUntilCondition(
            () => client.GetAsync($"/fork/{MultiForkSuccesses}/manifest"),
            async r => r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains(version),
            maxAttempts: 30
        );

        // 6. Verify — file is accessible
        var getResponse = await client.GetAsync($"/fork/{MultiForkSuccesses}/version/{version}/file/test.zip");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var data = await getResponse.Content.ReadAsByteArrayAsync();
        using var zipStream = new MemoryStream(data);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("data.txt");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        var actualContent = await reader.ReadToEndAsync();
        Assert.Equal("hello from multi-publish", actualContent);
    }

    [Fact]
    public async Task MultiPublishFinish_NoClientZip_ReturnsUnprocessableEntity()
    {
        var client = Factory.CreateClient();

        const string version = "101.0.0";

        // Start
        var startRequest = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiForkSuccesses}/publish/start")
        {
            Content = JsonContent.Create(new { Version = version, EngineVersion = "0.1.2" })
        };
        startRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        await client.SendAsync(startRequest);

        // Upload only a server zip, no client zip
        await UploadMultiPublishFile(client, version, "SS14.Server_linux-x64.zip", MultiForkSuccesses, CreateServerZip("linux-only"));

        // Finish → should fail
        var finishRequest = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiForkSuccesses}/publish/finish")
        {
            Content = JsonContent.Create(new { Version = version })
        };
        finishRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var finishResponse = await client.SendAsync(finishRequest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, finishResponse.StatusCode);
        var body = await finishResponse.Content.ReadAsStringAsync();
        Assert.Contains("no client zip was provided", body);
    }

    [Fact]
    public async Task MultiPublishFinish_VersionNotStarted_ReturnsNotFound()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiForkSuccesses}/publish/finish")
        {
            Content = JsonContent.Create(new { Version = "5.0.0" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MultiPublishFile_DuplicateFile_ReturnsConflict()
    {
        var client = Factory.CreateClient();

        const string version = "102.0.0";

        // Start
        var startRequest = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiForkSuccesses}/publish/start")
        {
            Content = JsonContent.Create(new { Version = version, EngineVersion = "0.1.2" })
        };
        startRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        await client.SendAsync(startRequest);

        // Upload file
        await UploadMultiPublishFile(client, version, "test.zip", MultiForkSuccesses, CreateClientZip("first"));

        // Upload same file again
        var content = new ByteArrayContent(CreateClientZip("duplicate"));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{MultiForkSuccesses}/publish/file")
        {
            Content = content
        };
        request.Headers.Add("Robust-Cdn-Publish-File", "test.zip");
        request.Headers.Add("Robust-Cdn-Publish-Version", version);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    #endregion

    #region Helpers

    private async Task UploadMultiPublishFile(HttpClient client, string version, string fileName, string forkName, byte[] fileContent)
    {
        var content = new ByteArrayContent(fileContent);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{forkName}/publish/file")
        {
            Content = content
        };
        request.Headers.Add("Robust-Cdn-Publish-File", fileName);
        request.Headers.Add("Robust-Cdn-Publish-Version", version);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static byte[] CreateClientZip(string content)
    {
        using var memStream = new MemoryStream();
        using (var zip = new ZipArchive(memStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("data.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return memStream.ToArray();
    }

    private static byte[] CreateServerZip(string platform)
    {
        using var memStream = new MemoryStream();
        using (var zip = new ZipArchive(memStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("Robust.Server.dll");
            using var writer = new StreamWriter(entry.Open());
            writer.Write($"fake server binary for {platform}");
        }
        return memStream.ToArray();
    }

    private static async Task<string> ReadFileContent(HttpResponseMessage getResponse)
    {
        var data = await getResponse.Content.ReadAsByteArrayAsync();
        using var zipStream = new MemoryStream(data);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("data.txt");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        return await reader.ReadToEndAsync();
    }

    #endregion
}
