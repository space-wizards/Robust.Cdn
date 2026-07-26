using Robust.Cdn.DataAccessLayer;

namespace Robust.Cdn.Services;

public sealed class PublishManager(
    ManifestDatabase manifestDatabase,
    BuildDirectoryManager buildDirectoryManager,
    ILogger<PublishManager> logger)
{
    public void AbortMultiPublish(string fork, string version)
    {
        logger.LogDebug("Aborting publish for fork {Fork}, version {version}", fork, version);

        // Drop record from database.
        manifestDatabase.DeleteVersionByVersionName(fork, version);

        // Delete directory on disk.
        var versionDir = buildDirectoryManager.GetBuildVersionPath(fork, version);
        if (Directory.Exists(versionDir))
            Directory.Delete(versionDir, recursive: true);
    }
}
