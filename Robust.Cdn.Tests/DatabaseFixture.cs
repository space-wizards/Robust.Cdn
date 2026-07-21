using System.IO.Compression;

namespace Robust.Cdn.Tests;

/// <summary>
/// Collection fixture that tracks all temp database files created during a test collection
/// and cleans them up when the collection is done.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    public string CreateTempDb()
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Creates a fork directory with a version directory and a client zip for the ingest job to find.
    /// Returns the base path that should be used as FileDiskPath.
    /// </summary>
    public string CreateTestVersionOnDisk(
        string fork,
        string version,
        string? content = null,
        string clientZipName = "test"
    )
    {
        content ??= "test content";
        var baseDir = Path.Combine(Path.GetTempPath(), "RobustCdnTests", Guid.NewGuid().ToString());
        var versionDir = Path.Combine(baseDir, fork, version);
        Directory.CreateDirectory(versionDir);
        _tempDirs.Add(baseDir);

        var zipPath = Path.Combine(versionDir, $"{clientZipName}.zip");
        using (var zipStream = File.Create(zipPath))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("test.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return baseDir;
    }

    /// <summary>
    /// Creates a fork directory with a version containing a client zip that has multiple files.
    /// Each file has unique content (file name as content).
    /// Returns the base path that should be used as FileDiskPath.
    /// </summary>
    public string CreateTestVersionWithMultipleFilesOnDisk(
        string fork,
        string version,
        Func<string, string>? contentFactory = null,
        string clientZipName = "test",
        IReadOnlyList<string>? fileNames = null)
    {
        contentFactory ??= (fn) => fn + " test content1";
        fileNames ??= ["alpha.txt", "beta.txt", "gamma.txt"];

        var baseDir = Path.Combine(Path.GetTempPath(), "RobustCdnTests", Guid.NewGuid().ToString());
        var versionDir = Path.Combine(baseDir, fork, version);
        Directory.CreateDirectory(versionDir);
        _tempDirs.Add(baseDir);

        var zipPath = Path.Combine(versionDir, $"{clientZipName}.zip");
        using (var zipStream = File.Create(zipPath))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            foreach (var fileName in fileNames)
            {
                var entry = zip.CreateEntry(fileName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(contentFactory(fileName));
            }
        }

        return baseDir;
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }

        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

