using Microsoft.AspNetCore.Mvc.Testing;
using SharpZstd;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Xunit.Abstractions;

namespace Robust.Cdn.Tests;

/// <summary>
/// Base class for integration tests providing a pre-configured <c>WebApplicationFactory</c>
/// and automatic cleanup of temp database files after the test collection finishes.
/// </summary>
[Collection("ControllerTests")]
public abstract class TestBase : IClassFixture<DatabaseFixture>
{
    protected static readonly DateTimeOffset FixedLastWriteTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    protected WebApplicationFactory<Program> Factory { get; }
    protected DatabaseFixture Database { get; }
    protected string ManifestDbPath { get; }

    protected abstract string ForkName { get; }

    protected TestBase(WebApplicationFactory<Program> factory, DatabaseFixture database, ITestOutputHelper testOutput)
    {
        Database = database;

        var config = new Dictionary<string, string?>(GetDefaultConfiguration());
        foreach (var (key, value) in GetConfigurationOverrides())
        {
            config[key] = value;
        }

        ManifestDbPath = config["Manifest:DatabaseFileName"]!;

        Factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(config);
            });

            builder.ConfigureLogging(logging =>
            {
                logging.AddProvider(new XUnitLoggerProvider(testOutput));
            });
        });
    }

    /// <summary>
    /// Provides default configuration for all tests.
    /// Override to change defaults globally.
    /// </summary>
    private Dictionary<string, string?> GetDefaultConfiguration()
    {
        return new()
        {
            ["Cdn:DatabaseFileName"] = Database.CreateTempDb(),
            ["Manifest:DatabaseFileName"] = Database.CreateTempDb(),
            ["Manifest:FileDiskPath"] = Path.GetTempPath(),
            [$"Manifest:Forks:{ForkName}:ClientZipName"] = "test",
            [$"Manifest:Forks:{ForkName}:BuildsPageLinkText"] = "test",
        };
    }

    /// <summary>
    /// Override to add or replace specific configuration keys for a particular test class.
    /// Returned values take precedence over <c>GetDefaultConfiguration()</c>.
    /// Please do not use <see cref="Factory"/> there, as this method is used
    /// in c-tor or base class and factory won't be set up yet (config is required for factory).
    /// </summary>
    protected virtual Dictionary<string, string?> GetConfigurationOverrides() => new();

    /// <summary>
    /// Polls a request until it returns OK or the timeout is reached.
    /// Useful for waiting on async jobs triggered during startup (e.g. version ingestion).
    /// </summary>
    protected static async Task<HttpResponseMessage> PollUntilCondition(
        Func<Task<HttpResponseMessage>> factory,
        Func<HttpResponseMessage, Task<bool>> condition,
        int maxAttempts = 20,
        int delayMs = 200)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            var response = await factory();
            if (await condition(response))
                return response;

            await Task.Delay(delayMs);
        }

        throw new TimeoutException($"Request did not return OK after {maxAttempts * delayMs}ms");
    }

    /// <summary>
    /// Polls a GET request until it returns OK.
    /// </summary>
    protected static Task<HttpResponseMessage> PollUntilOk(Func<Task<HttpResponseMessage>> factory, int maxAttempts = 20, int delayMs = 200)
    {
        return PollUntilCondition(factory, response => Task.FromResult(response.StatusCode == HttpStatusCode.OK), maxAttempts, delayMs);
    }

    /// <summary>
    /// Decompresses response content using zstd.
    /// Returns stream as bytes array.
    /// </summary>
    protected static async Task<byte[]> DecompressBody(HttpResponseMessage response)
    {
        await using var compressedStream = await response.Content.ReadAsStreamAsync();
        await using var decompressStream = new ZstdDecodeStream(compressedStream, leaveOpen: false);
        using var memStream = new MemoryStream();
        await decompressStream.CopyToAsync(memStream);
        return memStream.ToArray();
    }

    /// <summary> Start host so CDN can download release fils then poke CDN to publish new release. </summary>
    protected async Task PublishOneShotRelease(
        HttpClient client,
        string archivePath,
        string bearerToken,
        string? version = null,
        int basePort = 18765
    )
    {
        await using var serverTask = new FileProvidingTemporaryHost(basePort, archivePath);

        var publishRequest = new
        {
            Version = version ?? "2.0.0",
            EngineVersion = "0.1.2",
            Archive = $"http://127.0.0.1:{basePort}/archive.zip"
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/fork/{ForkName}/publish")
        {
            Content = JsonContent.Create(publishRequest)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var publishResponse = await client.SendAsync(request);
        publishResponse.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates a ZIP archive on disk containing a client zip, server zips, and an extra file.
    /// Used to simulate a CI build artifact for publish tests.
    /// </summary>
    protected static void CreatePublishArchive(string archivePath, string clientZipName, string content)
    {
        using (var archiveStream = File.Create(archivePath))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
        {
            // Client zip (required by publish)
            var clientEntry = archive.CreateEntry($"{clientZipName}.zip");
            clientEntry.LastWriteTime = FixedLastWriteTime;
            using (var clientEntryStream = clientEntry.Open())
            using (var clientZip = new ZipArchive(clientEntryStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var fileEntry = clientZip.CreateEntry("data.txt");
                fileEntry.LastWriteTime = FixedLastWriteTime;
                using var writer = new StreamWriter(fileEntry.Open());
                writer.Write(content);
            }

            // Server zips
            var serverZipNames = new[] { "SS14.Server_win-x64.zip", "SS14.Server_linux-x64.zip" };
            foreach (var serverName in serverZipNames)
            {
                var serverEntry = archive.CreateEntry(serverName);
                serverEntry.LastWriteTime = FixedLastWriteTime;
                using var serverEntryStream = serverEntry.Open();
                using var serverZip = new ZipArchive(serverEntryStream, ZipArchiveMode.Create, leaveOpen: true);
                var serverFile = serverZip.CreateEntry("Robust.Server.dll");
                serverFile.LastWriteTime = FixedLastWriteTime;
                using var serverWriter = new StreamWriter(serverFile.Open());
                serverWriter.Write($"fake server binary for {serverName}");
            }

            // Extra file that should be skipped by ClassifyEntries
            var extraEntry = archive.CreateEntry("readme.txt");
            extraEntry.LastWriteTime = FixedLastWriteTime;
            using (var extraWriter = new StreamWriter(extraEntry.Open()))
            {
                extraWriter.Write("this file should be ignored by publish");
            }
        }
    }

    /// <summary>
    /// Creates a ZIP archive containing only a server zip (no client zip).
    /// Used to test the "Client zip is missing" error in publish.
    /// </summary>
    protected static void CreateServerOnlyArchive(string archivePath)
    {
        using var archiveStream = File.Create(archivePath);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);

        var serverEntry = archive.CreateEntry("SS14.Server_linux-x64.zip");
        serverEntry.LastWriteTime = FixedLastWriteTime;
        using var serverEntryStream = serverEntry.Open();
        using var serverZip = new ZipArchive(serverEntryStream, ZipArchiveMode.Create, leaveOpen: true);
        var serverFile = serverZip.CreateEntry("Robust.Server.dll");
        serverFile.LastWriteTime = FixedLastWriteTime;
        using var serverWriter = new StreamWriter(serverFile.Open());
        serverWriter.Write("fake server binary");
    }

    /// <summary>
    /// Simple listener for providing static file on request.
    /// Is used due to publish process requiring a URL to download new version as archive.
    /// </summary>
    protected class FileProvidingTemporaryHost : IAsyncDisposable
    {
        private readonly string _filePath;
        private readonly CancellationTokenSource _cts = new();
        private readonly HttpListener _listener;
        private readonly Task _task;

        public FileProvidingTemporaryHost(int port, string filePath)
        {
            _filePath = filePath;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _task = Task.Factory.StartNew(Loop);
        }

        private async Task Loop()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(token);
                await using var file = File.OpenRead(_filePath);
                ctx.Response.ContentType = "application/zip";
                ctx.Response.ContentLength64 = file.Length;
                await file.CopyToAsync(ctx.Response.OutputStream, token);
                ctx.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Dispose();
            _listener.Stop();
            await _task;
        }
    }
}

