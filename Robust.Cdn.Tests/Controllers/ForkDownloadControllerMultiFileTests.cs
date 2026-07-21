using Microsoft.AspNetCore.Mvc.Testing;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Integration tests for <c>DownloadController</c> with multiple files in the version.
///
/// Endpoint: POST /fork/{fork}/version/{version}/download
/// </summary>
[Trait("Category", "IntegrationTest")]
public sealed class ForkDownloadControllerMultiFileTests(
    WebApplicationFactory<Program> factory, 
    DatabaseFixture database, 
    ITestOutputHelper testOutput
) : TestBase(factory, database, testOutput)
{
    private const string MultiFileFork = "testfork-multi";
    private const string MultiFileVersion = "1.0.0";
    private readonly Func<string, string> _contentTextFactory = fn => fn + " test content!";

    private static readonly string[] FileNames = ["alpha.txt", "beta.txt", "gamma.txt"];

    protected override string ForkName => MultiFileFork;

    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        return new()
        {
            ["Manifest:FileDiskPath"] = Database.CreateTestVersionWithMultipleFilesOnDisk(
                MultiFileFork,
                MultiFileVersion,
                contentFactory: _contentTextFactory,
                fileNames: FileNames
            ),
        };
    }

    [Fact]
    public async Task DownloadPost_ValidRequestWithMultipleFiles_ReturnsAllFiles()
    {
        var client = Factory.CreateClient();

        // Request all 3 files
        var body = BuildDownloadRequestBody(0, 1, 2);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/{MultiFileVersion}/download")
            {
                Content = content
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            return client.SendAsync(request);
        });

        var responseBody = await response.Content.ReadAsByteArrayAsync();

        Assert.True(responseBody.Length >= 4, "Response must contain stream header");
        var streamFlags = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(0, 4));
        Assert.Equal(0, streamFlags); // No flags (no pre-compression, no stream compression)

        var offset = 4;

        for (var i = 0; i < FileNames.Length; i++)
        {
            // File header: 4 bytes for uncompressed size
            Assert.True(responseBody.Length >= offset + 4, $"Response too short for file {i} header");
            var fileSize = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(offset, 4));
            offset += 4;

            var expected = _contentTextFactory(FileNames[i]);
            var expectedSize = System.Text.Encoding.UTF8.GetByteCount(expected);
            Assert.Equal(expectedSize, fileSize);

            // File data
            Assert.True(responseBody.Length >= offset + fileSize, $"Response too short for file {i} data");
            var fileContent = System.Text.Encoding.UTF8.GetString(responseBody, offset, fileSize);
            Assert.Equal(expected, fileContent);
            offset += fileSize;
        }
    }

    [Fact]
    public async Task DownloadPost_MultipleFilesWithPreCompression_ReturnsPreCompressedStream()
    {
        // Request 2 out of 3 files. With AutoStreamCompressRatio = 2.0,
        // ratio 2/3 = 0.67 <= 2.0 -> else branch (pre-compression).
        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Cdn:AutoStreamCompressRatio"] = "2.0",
                });
            });
        });

        var client = factory.CreateClient();
        var body = BuildDownloadRequestBody(0, 2); // First and third files
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/{MultiFileVersion}/download")
            {
                Content = content
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            return client.SendAsync(request);
        });

        var responseBody = await response.Content.ReadAsByteArrayAsync();

        // Stream header: PreCompressed flag (1)
        Assert.True(responseBody.Length >= 4);
        var streamFlags = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(0, 4));
        Assert.Equal(1, streamFlags); // PreCompressed = 1

        var offset = 4;
        var expectedFileNames = new[] { "alpha.txt", "gamma.txt" };

        for (var i = 0; i < expectedFileNames.Length; i++)
        {
            // File header: 8 bytes
            Assert.True(responseBody.Length >= offset + 8, $"Response too short for file {i} header");
            var fileSize = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(offset, 4));
            var compInfo = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(offset + 4, 4));
            offset += 8;

            var expected = _contentTextFactory(expectedFileNames[i]);
            var expectedSize = System.Text.Encoding.UTF8.GetByteCount(expected);
            Assert.Equal(expectedSize, fileSize);
            Assert.Equal(0, compInfo); // Not ZStd compressed in DB

            // File data
            Assert.True(responseBody.Length >= offset + fileSize, $"Response too short for file {i} data");
            var fileContent = System.Text.Encoding.UTF8.GetString(responseBody, offset, fileSize);
            Assert.Equal(expected, fileContent);
            offset += fileSize;
        }
    }

    [Fact]
    public async Task DownloadPost_MultipleFilesWithStreamCompression_ReturnsCompressedStream()
    {
        var client = Factory.CreateClient();
        var body = BuildDownloadRequestBody(0, 1, 2); // All 3 files
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/{MultiFileVersion}/download")
            {
                Content = content
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));
            return client.SendAsync(request);
        });

        // Verify stream-level compression
        Assert.Equal("zstd", response.Content.Headers.GetValues("Content-Encoding").Single());

        // Decompress
        var decompressedBody = await DecompressBody(response);

        // Stream header (4 bytes) — no pre-compression flag
        Assert.True(decompressedBody.Length >= 4);
        var streamFlags = BinaryPrimitives.ReadInt32LittleEndian(decompressedBody.AsSpan(0, 4));
        Assert.Equal(0, streamFlags); // No pre-compression since stream compression chosen

        // Parse each file (file header 4 bytes + file data)
        var offset = 4;
        var expectedFileNames = new[] { "alpha.txt", "beta.txt", "gamma.txt" };

        for (var i = 0; i < expectedFileNames.Length; i++)
        {
            Assert.True(decompressedBody.Length >= offset + 4, $"Response too short for file {i} header");
            var fileSize = BinaryPrimitives.ReadInt32LittleEndian(decompressedBody.AsSpan(offset, 4));
            offset += 4;

            var expected = _contentTextFactory(expectedFileNames[i]);
            var expectedSize = System.Text.Encoding.UTF8.GetByteCount(expected);
            Assert.Equal(expectedSize, fileSize);

            Assert.True(decompressedBody.Length >= offset + fileSize, $"Response too short for file {i} data");
            var fileContent = System.Text.Encoding.UTF8.GetString(decompressedBody, offset, fileSize);
            Assert.Equal(expected, fileContent);
            offset += fileSize;
        }
    }

    /// <summary>
    /// Tests that requesting a duplicate file index returns BadRequest.
    /// First waits for the version to be ingested (by performing a valid request).
    /// </summary>
    [Fact]
    public async Task DownloadPost_DuplicateFileIndex_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();

        // First ensure the version is ingested
        var validBody = BuildDownloadRequestBody(0);
        var validContent = new ByteArrayContent(validBody);
        validContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await PollUntilOk(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/{MultiFileVersion}/download")
            {
                Content = validContent
            };
            req.Headers.Add("X-Robust-Download-Protocol", "1");
            return client.SendAsync(req);
        });

        // Now request file 0 twice
        var body = BuildDownloadRequestBody(0, 0);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/{MultiFileVersion}/download")
        {
            Content = content
        };
        request.Headers.Add("X-Robust-Download-Protocol", "1");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DownloadPost_OutOfBoundsIndex_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();

        // First ensure the version is ingested
        var validBody = BuildDownloadRequestBody(0);
        var validContent = new ByteArrayContent(validBody);
        validContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await PollUntilOk(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/{MultiFileVersion}/download")
            {
                Content = validContent
            };
            req.Headers.Add("X-Robust-Download-Protocol", "1");
            return client.SendAsync(req);
        });

        // Request file index 99 which is out of bounds (only 3 files)
        var body = BuildDownloadRequestBody(99);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/{MultiFileVersion}/download")
        {
            Content = content
        };
        request.Headers.Add("X-Robust-Download-Protocol", "1");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DownloadPost_NegativeIndex_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();

        // First ensure the version is ingested
        var validBody = BuildDownloadRequestBody(0);
        var validContent = new ByteArrayContent(validBody);
        validContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await PollUntilOk(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/{MultiFileVersion}/download")
            {
                Content = validContent
            };
            req.Headers.Add("X-Robust-Download-Protocol", "1");
            return client.SendAsync(req);
        });

        // Now request with negative index
        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, -1);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/version/{MultiFileVersion}/download")
        {
            Content = content
        };
        request.Headers.Add("X-Robust-Download-Protocol", "1");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Builds a download request body with a list of file indices (each as a 4-byte LE int).
    /// </summary>
    private static byte[] BuildDownloadRequestBody(params int[] indices)
    {
        var body = new byte[indices.Length * 4];
        for (var i = 0; i < indices.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(i * 4, 4), indices[i]);
        }
        return body;
    }
}
