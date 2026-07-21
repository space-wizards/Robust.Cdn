using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Robust.Cdn.Config;
using SharpZstd;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Base class for testing both <c>DownloadController</c> (via /fork/{fork}/version/{version}/...)
/// and <c>DownloadCompatibilityController</c> (via /version/{version}/...).
/// </summary>
[Trait("Category", "IntegrationTest")]
public abstract class DownloadControllerTestBase(
    WebApplicationFactory<Program> factory, 
    DatabaseFixture database, 
    ITestOutputHelper testOutput
) : TestBase(factory, database, testOutput)
{
    protected const string FileContent = "test content";
    protected const string ExistingVersion = "1.0.0";

    /// <summary>
    /// URL prefix for the controller under test, without trailing slash and without version.
    /// </summary>
    protected abstract string RoutePrefix { get; }

    #region Get Manifest

    [Fact]
    public async Task GetManifest_ExistingVersion_ReturnsOkWithManifestHash()
    {
        var client = Factory.CreateClient();
        var response = await PollUntilOk(() => client.GetAsync($"{RoutePrefix}/{ExistingVersion}/manifest"));

        Assert.True(response.Headers.Contains("X-Manifest-Hash"));
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Robust Content Manifest", lines[0]);
        Assert.Equal("8D3BD9BFD005F4179699B9212273284C6DE851AE910EEE2FD1BB0D96EDE65A3D test.txt", lines[1]);
    }

    [Fact]
    public async Task GetManifest_WithZstdEncoding_ReturnsCompressedManifest()
    {
        var client = Factory.CreateClient();
        var response = await PollUntilOk(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{RoutePrefix}/{ExistingVersion}/manifest");
            req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));
            return client.SendAsync(req);
        });

        Assert.Equal("zstd", response.Content.Headers.GetValues("Content-Encoding").Single());
        Assert.True(response.Headers.Contains("X-Manifest-Hash"));

        await using var bodyStream = await response.Content.ReadAsStreamAsync();
        await using var decompressStream = new ZstdDecodeStream(bodyStream, leaveOpen: false);
        using var reader = new StreamReader(decompressStream);
        var body = await reader.ReadToEndAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Robust Content Manifest", lines[0]);
        Assert.Equal("8D3BD9BFD005F4179699B9212273284C6DE851AE910EEE2FD1BB0D96EDE65A3D test.txt", lines[1]);
    }

                #endregion

    #region Options Download

    [Fact]
    public async Task DownloadOptions_ReturnsNoContentWithProtocolHeaders()
    {
        var client = Factory.CreateClient();
        var response = await client.SendAsync(new(HttpMethod.Options, $"{RoutePrefix}/{ExistingVersion}/download"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var minProtocol = int.Parse(response.Headers.GetValues("X-Robust-Download-Min-Protocol").Single());
        var maxProtocol = int.Parse(response.Headers.GetValues("X-Robust-Download-Max-Protocol").Single());

        Assert.True(minProtocol <= maxProtocol,
            $"Min protocol ({minProtocol}) must be less than or equal to max protocol ({maxProtocol})");
    }

    #endregion

    #region Post Download

    [Fact]
    public async Task DownloadPost_WrongContentType_ReturnsBadRequest()
    {
        var client = Factory.CreateClient();
        var content = new StringContent("test");
        var response = await client.PostAsync($"{RoutePrefix}/{ExistingVersion}/download", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task DownloadPost_InvalidProtocol_ReturnsBadRequest(string protocol)
    {
        var client = Factory.CreateClient();
        var content = new ByteArrayContent(new byte[4]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Post, $"{RoutePrefix}/{ExistingVersion}/download")
        {
            Content = content
        };
        request.Headers.Add("X-Robust-Download-Protocol", protocol);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DownloadPost_ValidRequest_ReturnsOkWithStreamContent()
    {
        var client = Factory.CreateClient();

        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, 0);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{RoutePrefix}/{ExistingVersion}/download")
            {
                Content = content
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            return client.SendAsync(request);
        });

        var responseBody = await response.Content.ReadAsByteArrayAsync();
        Assert.True(responseBody.Length >= 4, "Response must contain at least the stream header");

        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(FileContent);
        var fileSize = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(4, 4));
        Assert.Equal(expectedSize, fileSize);

        var fileData = System.Text.Encoding.UTF8.GetString(responseBody, 8, fileSize);
        Assert.Equal(FileContent, fileData);
    }

    [Fact]
    public async Task DownloadPost_WithZstdStreamCompression_ReturnsCompressedStream()
    {
        var client = Factory.CreateClient();
        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, 0);

        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{RoutePrefix}/{ExistingVersion}/download")
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
                }
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));
            return client.SendAsync(request);
        });

        Assert.Equal("zstd", response.Content.Headers.GetValues("Content-Encoding").Single());

        var decompressedBody = await DecompressBody(response);

        Assert.True(decompressedBody.Length >= 8, "Decompressed stream must contain header + file header");

        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(FileContent);
        var fileSize = BinaryPrimitives.ReadInt32LittleEndian(decompressedBody.AsSpan(4, 4));
        Assert.Equal(expectedSize, fileSize);

        var fileData = decompressedBody[8..];
        var fileContent = System.Text.Encoding.UTF8.GetString(fileData);
        Assert.Equal(FileContent, fileContent);
    }

    [Fact]
    public async Task DownloadPost_WithAutoRatioOptingForPreCompression_ReturnsPreCompressedStream()
    {
        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, 0);

        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"Cdn:{nameof(CdnOptions.AutoStreamCompressRatio)}"] = "2.0",
                });
            });
        });

        var client = factory.CreateClient();
        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{RoutePrefix}/{ExistingVersion}/download")
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
                }
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            return client.SendAsync(request);
        });

        var responseBody = await response.Content.ReadAsByteArrayAsync();

        Assert.True(responseBody.Length >= 4, "Response must contain stream header");
        var streamHeaderFlags = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(0, 4));
        Assert.Equal(1, streamHeaderFlags);

        Assert.True(responseBody.Length >= 12, "Response must contain full file header");
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(FileContent);
        var fileSize = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(4, 4));
        Assert.Equal(expectedSize, fileSize);
        var compInfo = BinaryPrimitives.ReadInt32LittleEndian(responseBody.AsSpan(8, 4));
        Assert.Equal(0, compInfo);

        var fileContent = System.Text.Encoding.UTF8.GetString(responseBody[12..]);
        Assert.Equal(FileContent, fileContent);
    }

    [Fact]
    public async Task DownloadPost_WithAutoStreamCompressRatioDisabled_StreamCompressFallback()
    {
        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"Cdn:{nameof(CdnOptions.AutoStreamCompressRatio)}"] = "-1",
                    [$"Cdn:{nameof(CdnOptions.StreamCompress)}"] = "true",
                    [$"Cdn:{nameof(CdnOptions.SendPreCompressed)}"] = "false",
                });
            });
        });

        var client = factory.CreateClient();
        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, 0);

        var response = await PollUntilOk(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{RoutePrefix}/{ExistingVersion}/download")
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
                }
            };
            request.Headers.Add("X-Robust-Download-Protocol", "1");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));
            return client.SendAsync(request);
        });

        Assert.Equal("zstd", response.Content.Headers.GetValues("Content-Encoding").Single());

        var decompressedBody = await DecompressBody(response);

        Assert.True(decompressedBody.Length >= 8, "Decompressed stream must contain header + file header");
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(FileContent);
        var fileSize = BinaryPrimitives.ReadInt32LittleEndian(decompressedBody.AsSpan(4, 4));
        Assert.Equal(expectedSize, fileSize);

        var fileData = decompressedBody[8..];
        var fileContent = System.Text.Encoding.UTF8.GetString(fileData);
                Assert.Equal(FileContent, fileContent);
    }

    #endregion
}
