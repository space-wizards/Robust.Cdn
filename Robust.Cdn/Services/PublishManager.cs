using System.Data.Common;
using Dapper;

namespace Robust.Cdn.Services;

public sealed class PublishManager(
    ManifestDatabase manifestDatabase,
    BuildDirectoryManager buildDirectoryManager,
    ILogger<PublishManager> logger)
{
    public void AbortMultiPublish(string fork, string version, DbTransaction tx, bool commit)
    {
        logger.LogDebug("Aborting publish for fork {Fork}, version {version}", fork, version);

        // Drop record from database.
        var dbCon = manifestDatabase.Connection;
        dbCon.Execute("""
            DELETE FROM PublishInProgress
            WHERE Version = @Version
                AND ForkId IN (
                    SELECT Id FROM Fork WHERE Name = @Fork
                )
            """, new { Version = version, Fork = fork }, tx);

        // Delete directory on disk.
        var versionDir = buildDirectoryManager.GetBuildVersionPath(fork, version);
        if (Directory.Exists(versionDir))
            Directory.Delete(versionDir, recursive: true);

        if (commit)
            tx.Commit();
    }
}
