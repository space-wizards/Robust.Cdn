using Microsoft.AspNetCore.Mvc.Testing;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

[Trait("Category", "IntegrationTest")]
public sealed class ForkManifestControllerTests(
    WebApplicationFactory<Program> factory, 
    DatabaseFixture database, 
    ITestOutputHelper testOutput
) : TestBase(factory, database, testOutput)
{
    protected override string ForkName => "testfork2";
    private const string Token = "s3cret";
    private string? _fileDiskPath;

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        _fileDiskPath ??= Database.CreateTestVersionOnDisk(ForkName, "1.0.0");
        return new()
        {
            ["Manifest:FileDiskPath"] = _fileDiskPath,
            [$"Manifest:Forks:{ForkName}:Private"] = "false",
            [$"Manifest:Forks:{ForkName}:UpdateToken"] = Token,
            ["BaseUrl"] = "http://localhost/",
        };
    }

    [Fact]
    public async Task GetManifest_ForkNotInConfiguration_ReturnsNotFound()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync("/fork/nonexistent/manifest");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetManifest_PublicForkNoManifestCache_ReturnsNotFound()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync($"/fork/{ForkName}/manifest");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetManifest_PublicForkWithCache_FromPublishFlow_ReturnsJson()
    {
        var client = Factory.CreateClient();

        const string postVersion = "2.0.3";
        await SetupPublishedBuild(client, postVersion);

        // Poll for the manifest to appear with build data.
        var manifestResponse = await PollUntilCondition(
            () => client.GetAsync($"/fork/{ForkName}/manifest"),
            async response =>
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    return false;

                var body = await response.Content.ReadAsStringAsync();
                return body.Contains(postVersion);
            }
        );

        Assert.Equal("application/json", manifestResponse.Content.Headers.ContentType?.MediaType);

        var body = await manifestResponse.Content.ReadAsStringAsync();
        var bodyElements = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.True(bodyElements.TryGetProperty("builds", out var builds));
        Assert.True(builds.TryGetProperty(postVersion, out var version));
        Assert.True(version.TryGetProperty("client", out var c));

        Assert.True(c.TryGetProperty("url", out var clientUrl));
        Assert.Equal($"http://localhost/fork/{ForkName}/version/{postVersion}/file/test.zip", clientUrl.GetString());

        Assert.True(c.TryGetProperty("sha256", out var clientHash));
        Assert.Matches("^[0-9A-F]{64}$", clientHash.GetString());

        Assert.True(version.TryGetProperty("server", out var s));
        Assert.Equal(2, s.GetPropertyCount());

        Assert.True(s.TryGetProperty("linux-x64", out var linux));
        Assert.True(linux.TryGetProperty("url", out var linuxUrl));
        Assert.Equal($"http://localhost/fork/{ForkName}/version/{postVersion}/file/SS14.Server_linux-x64.zip", linuxUrl.GetString());

        Assert.True(linux.TryGetProperty("sha256", out var linuxHash));
        // Server zip hashes cannot be verified with pre-computed values because the publish flow
        // injects build.json into server zips after extraction, modifying their content and hash.
        Assert.Matches("^[0-9A-F]{64}$", linuxHash.GetString());

        Assert.True(linux.TryGetProperty("size", out var linuxSize));
        Assert.Equal(535, linuxSize.GetInt64());

        Assert.True(s.TryGetProperty("win-x64", out var win));
        Assert.True(win.TryGetProperty("url", out var winUrl));
        Assert.Equal($"http://localhost/fork/{ForkName}/version/{postVersion}/file/SS14.Server_win-x64.zip", winUrl.GetString());

        Assert.True(win.TryGetProperty("sha256", out var winHash));
        // Same as linux: hash is modified by build.json injection, only format is verified.
        Assert.Matches("^[0-9A-F]{64}$", winHash.GetString());

        Assert.True(win.TryGetProperty("size", out var winSize));
        Assert.Equal(533, winSize.GetInt64());
    }

    [Fact]
    public async Task GetFile_ForkNotInConfiguration_ReturnsNotFound()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync("/fork/nonexistent/version/1.0.0/file/test.zip");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetFile_VersionDoesNotExist_ReturnsNotFound()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync($"/fork/{ForkName}/version/9.9.9/file/test.zip");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetFile_FileOnDiskMissing_ReturnsInternalServerError()
    {
        var client = Factory.CreateClient();
        const string version = "2.0.1";
        await SetupPublishedBuild(client, version);

        var response = await client.GetAsync($"/fork/{ForkName}/version/{version}/file/nonexistent.zip");
        // PhysicalFile throws when file doesn't exist, resulting in a 500
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Theory]
    [InlineData("test.zip", "data.txt", "hello from publish")]
    [InlineData("SS14.Server_linux-x64.zip", "Robust.Server.dll", "fake server binary for SS14.Server_linux-x64.zip")]
    [InlineData("SS14.Server_win-x64.zip", "Robust.Server.dll", "fake server binary for SS14.Server_win-x64.zip")]
    public async Task GetFile_Success_ReturnsZipFile(string fileName, string expectedEntry, string expectedContent)
    {
        var client = Factory.CreateClient();

        const string version = "2.0.2";
        await SetupPublishedBuild(client, version);

        var response = await client.GetAsync($"/fork/{ForkName}/version/{version}/file/{fileName}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);

        var data = await response.Content.ReadAsByteArrayAsync();
        using var zipStream = new MemoryStream(data);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var entry = zip.GetEntry(expectedEntry);
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry.Open());
        var actualContent = await reader.ReadToEndAsync();
        Assert.Equal(expectedContent, actualContent);
    }

    private async Task SetupPublishedBuild(HttpClient client, string? version = null)
    {
        var archivePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        CreatePublishArchive(archivePath, clientZipName: "test", content: "hello from publish");
        await PublishOneShotRelease(client, archivePath, Token, version: version, basePort: 18765);
    }
}
