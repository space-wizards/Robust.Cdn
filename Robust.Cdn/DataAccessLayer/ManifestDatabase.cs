using Dapper;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.DataAccessLayer.Models;
using Robust.Cdn.DataAccessLayer.Sqlite.Blob;
using Robust.Cdn.Helpers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Robust.Cdn.DataAccessLayer;

/// <summary>
/// Database service for server manifest functionality.
/// </summary>
public sealed class ManifestDatabase(IOptions<ManifestOptions> options) : ScopedSqliteRepositoryBase(options.Value)
{
    private static readonly JsonSerializerOptions ManifestCacheContext = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary> Gets fork id by fork name. </summary>
    public int GetForkIdByName(string forkName)
    {
        return Connection.QuerySingle<int>("SELECT Id FROM Fork WHERE Name = @Name", new { Name = forkName });
    }

    /// <summary>
    /// Ensure all provided forks exists in manifest database.
    /// </summary>
    public void EnsureForksCreated(IEnumerable<string> forkNames)
    {
        Connection.Execute(
            "INSERT INTO Fork (Name) VALUES (@Name) ON CONFLICT DO NOTHING",
            forkNames.Select(x => new { Name = x }),
            Transaction
        );
    }

    /// <summary>
    /// Checks if there is a record of publish for build versin of fork already in progress.
    /// </summary>
    /// <param name="forkId">id of a fork.</param>
    /// <param name="requestVersion">Human-readable fork build version.</param>
    public bool IsPublishInProgress(int forkId, string requestVersion)
    {
        return Connection.QuerySingleOrDefault<bool>(
            "SELECT 1 FROM PublishInProgress WHERE Version = @Version AND ForkId = @ForkId",
            new { requestVersion, ForkId = forkId }
        );
    }

    /// <summary>
    /// Lists fork versions that were published before the specified date.
    /// </summary>
    public IReadOnlyList<VersionBriefData> QueryVersionOlderThen(string forkName, DateTime olderThen)
    {
        return Connection.Query<VersionBriefData>(
            """
            SELECT FV.Id, FV.Name
            FROM ForkVersion FV, Fork
            WHERE FV.ForkId = Fork.Id
              AND Fork.Name = @ForkName
              AND FV.PublishedTime < @PruneFrom
            """,
            new { ForkName = forkName, PruneFrom = olderThen },
            Transaction
        ).AsList();
    }

    /// <summary>
    /// Deletes fork versions by id.
    /// </summary>
    public int DeleteVersion(int id)
    {
        return Connection.Execute("DELETE FROM ForkVersion WHERE Id = @Id", id, Transaction);
    }

    /// <summary>
    /// Sets all mentioned versions of a fork as 'available'.
    /// </summary>
    /// <param name="forkName">Fork name.</param>
    /// <param name="versions">Human-readable fork build version number.</param>
    public void SetForkVersionsAvailable(string forkName, IEnumerable<string> versions)
    {
        var versionsArray = versions.ToArray();
        if(versionsArray.Length == 0)
            return;

        // sqlite does not, by default, support arrays inlining of more than 999 items.
        if (versionsArray.Length > 999)
            throw new ArgumentException("Cannot handle enumeration of more then 999 version.", nameof(versions));

        Connection.Execute(
            """
            UPDATE ForkVersion
            SET Available = TRUE
            WHERE Name IN @Versions
              AND ForkId = (SELECT Id FROM Fork WHERE Name = @ForkName)
            """,
            new
            {
                Versions = versionsArray,
                ForkName = forkName
            },
            Transaction
        );
    }

    /// <summary>
    /// Gets list of fork versions by fork name.
    /// </summary>
    public List<BuildVersionArtifactInfo> ListForkVersions(string forkName, int limit)
    {
        var dbVersions = Connection.Query<(int Id, string Name, DateTime PublishedTime, string EngineVersion)>(
            $"""
             SELECT FV.Id, FV.Name, PublishedTime, EngineVersion
             FROM ForkVersion FV
             INNER JOIN main.Fork F ON FV.ForkId = F.Id
             WHERE F.Name = @Fork
               AND FV.Available
             ORDER BY PublishedTime DESC
             LIMIT {limit}
             """,
            new { Fork = forkName },
            Transaction
        );

        var versions = new List<BuildVersionArtifactInfo>();
        foreach (var dbVersion in dbVersions)
        {
            var servers = Connection.Query<BuildVersionServerArtifactInfo>(
                """
                SELECT Platform, FileName, FileSize
                FROM ForkVersionServerBuild
                WHERE ForkVersionId = @ForkVersionId
                ORDER BY Platform
                """,
                new { ForkVersionId = dbVersion.Id },
                Transaction
            );

            var versionInfo = new BuildVersionArtifactInfo
            {
                Name = dbVersion.Name,
                EngineVersion = dbVersion.EngineVersion,
                PublishedTime = DateTime.SpecifyKind(dbVersion.PublishedTime, DateTimeKind.Utc),
                Servers = servers.ToArray()
            };
            versions.Add(versionInfo);
        }
        return versions;
    }

    /// <summary>
    /// Checks if fork version exists.
    /// </summary>
    /// <param name="forkName">Fork name.</param>
    /// <param name="versionNumber">Human-readable fork build version.</param>
    public bool IsVersionExists(string forkName, string versionNumber)
    {
        return Connection.QuerySingleOrDefault<bool>(
            """
            SELECT 1
            FROM ForkVersion, Fork
            WHERE ForkVersion.Name = @Version
              AND Fork.Name = @Fork
              AND Fork.Id = ForkVersion.ForkId
            """,
            new { Fork = forkName, Version = versionNumber },
            Transaction
        );
    }

    /// <summary>
    /// Insert build version info for provided files.
    /// </summary>
    /// <param name="clientArtifactBriefInfo">Artifact info for client side of artifacts.</param>
    /// <param name="diskFiles">New version build files.</param>
    /// <param name="forkName">Fork name for which versions are added.</param>
    /// <param name="metadata">Version metadata.</param>
    public void AddVersionsToDatabase(
        ArtifactBriefInfo clientArtifactBriefInfo,
        Dictionary<ArtifactBriefInfo, string> diskFiles,
        string forkName,
        VersionMetadata metadata
    )
    {
        var dbCon = Connection;

        var forkId = GetForkIdByName(forkName);

        var (clientName, clientSha256, _) = GetFileNameSha256Pair(diskFiles[clientArtifactBriefInfo]);

        var versionId = dbCon.QuerySingle<int>(
            """
            INSERT INTO ForkVersion (Name, ForkId, PublishedTime, ClientFileName, ClientSha256, EngineVersion)
            VALUES (@Name, @ForkId, @PublishTime, @ClientName, @ClientSha256, @EngineVersion)
            RETURNING Id
            """,
            new
            {
                Name = metadata.Version,
                ForkId = forkId,
                ClientName = clientName,
                ClientSha256 = clientSha256,
                metadata.EngineVersion,
                PublishTime = DateTime.UtcNow
            },
            Transaction
        );

        foreach (var (artifact, diskPath) in diskFiles)
        {
            if (artifact.Type != ArtifactType.Server)
                continue;

            var (serverName, serverSha256, fileSize) = GetFileNameSha256Pair(diskPath);

            dbCon.Execute(
                """
                INSERT INTO ForkVersionServerBuild (ForkVersionId, Platform, FileName, Sha256, FileSize)
                VALUES (@ForkVersion, @Platform, @ServerName, @ServerSha256, @FileSize)
                """,
                new
                {
                    ForkVersion = versionId,
                    artifact.Platform,
                    ServerName = serverName,
                    ServerSha256 = serverSha256,
                    FileSize = fileSize
                },
                Transaction
            );
        }
    }

    /// <summary>
    /// List all publishes in progress.
    /// </summary>
    public IReadOnlyList<PublishInProgressBriefInfo> ListPublishInProgress()
    {
        return Connection.Query<PublishInProgressBriefInfo>(
            """
            SELECT PublishInProgress.Id, Version, Fork.Name, StartTime
            FROM PublishInProgress
            INNER JOIN Fork ON Fork.Id = PublishInProgress.ForkId
            """,
            transaction: Transaction
        ).AsList();
    }

    /// <summary>
    /// Delete build version by fork name and human-readable build version number.
    /// </summary>
    public void DeleteVersionByVersionName(string forkName, string versionNumber)
    {
        Connection.Execute(
            """
            DELETE FROM PublishInProgress
            WHERE Version = @Version
                AND ForkId IN (
                    SELECT Id FROM Fork WHERE Name = @Fork
                )
            """,
            new { Version = versionNumber, Fork = forkName },
            Transaction
        );
    }

    /// <summary>
    /// Inserts publish in progress record with all required version metadata.
    /// </summary>
    public int InsertPublishInProgress(
        string versionNumber,
        string engineVersion,
        int forkId,
        SourceVersionInfo build,
        SourceVersionInfo engine
    )
    {

        return Connection.Execute(
            """
            INSERT INTO PublishInProgress (
                Version, 
                ForkId, 
                StartTime, 
                EngineVersion,
                SourceUrl,
                SourceCommitId,
                SourceBranchName,
                EngineSourceUrl,
                EngineSourceCommitId,
                EngineSourceBranchName 
            )
            VALUES (
                @Version, 
                @ForkId, 
                @StartTime, 
                @EngineVersion,
                @SourceUrl,
                @SourceCommitId,
                @SourceBranchName,
                @EngineSourceUrl,
                @EngineSourceCommitId,
                @EngineSourceBranchName 
            )
            """,
            new
            {
                Version = versionNumber,
                EngineVersion = engineVersion,
                ForkId = forkId,
                StartTime = DateTime.UtcNow,
                SourceUrl = build.SourceUrl,
                SourceCommitId = build.CommitId,
                SourceBranchName = build.BranchName,
                EngineSourceUrl = engine.SourceUrl,
                EngineSourceCommitId = engine.CommitId,
                EngineSourceBranchName = engine.BranchName,
            },
            Transaction
        );
    }

    /// <summary>
    /// Gets fork version full metadata.
    /// </summary>
    /// <param name="forkId">Id of a fork.</param>
    /// <param name="requestVersion">Human-readable fork version number.</param>
    /// <returns></returns>
    public VersionMetadata? GetVersionMetadata(int forkId, string requestVersion)
    {
        return Connection.QuerySingleOrDefault<VersionMetadata>(
            """
            SELECT Version, EngineVersion, SourceUrl, SourceCommitId, SourceBranchName, EngineSourceUrl, EngineSourceCommitId, EngineSourceBranchName 
            FROM PublishInProgress
            WHERE Version = @Name AND ForkId = @Fork
            """,
            new { Name = requestVersion, Fork = forkId }
        );
    }

    /// <summary>
    /// Updates server manifest cache for a fork.
    /// </summary>
    /// <param name="forkName">Fork name.</param>
    /// <param name="forkId">Fork id.</param>
    /// <param name="baseUrlManager">Helper for getting urls according to app current base url.</param>
    /// <returns>Number of affected rows.</returns>
    public int UpdateManifestCache(string forkName, int forkId, BaseUrlManager baseUrlManager)
    {

        var builds = new Dictionary<string, ManifestBuildData>();

        var versions = Connection
            .Query<(int id, string name, DateTime publishedTime, string clientFileName, byte[] clientSha256)>(
                """
                SELECT Id, Name, PublishedTime, ClientFileName, ClientSha256
                FROM ForkVersion
                WHERE Available AND ForkId = @ForkId
                """,
                new { ForkId = forkId }
            );

        foreach (var version in versions)
        {
            var buildData = new ManifestBuildData
            {
                PublishTime = DateTime.SpecifyKind(version.publishedTime, DateTimeKind.Utc),
                Client = new ManifestArtifact
                {
                    Url = baseUrlManager.MakeBuildInfoUrl($"fork/{forkName}/version/{version.name}/file/{version.clientFileName}"),
                    Sha256 = Convert.ToHexString(version.clientSha256)
                },
                Server = new Dictionary<string, ManifestArtifact>()
            };

            var servers = Connection.Query<(string platform, string fileName, byte[] sha256, long? size)>(
                """
                SELECT Platform, FileName, Sha256, FileSize
                FROM ForkVersionServerBuild
                WHERE ForkVersionId = @ForkVersionId
                """,
                new { ForkVersionId = version.id }
            );

            foreach (var (platform, fileName, sha256, size) in servers)
            {
                var manifestArtifact = new ManifestArtifact
                {
                    Url = baseUrlManager.MakeBuildInfoUrl($"fork/{forkName}/version/{version.name}/file/{fileName}"),
                    Sha256 = Convert.ToHexString(sha256),
                    Size = size
                };
                buildData.Server.Add(platform, manifestArtifact);
            }

            builds.Add(version.name, buildData);
        }

        var data = new ManifestData { Builds = builds };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, ManifestCacheContext);
        return Connection.Execute(
            "UPDATE Fork SET ServerManifestCache = @Data WHERE Id = @ForkId",
            new
            {
                Data = bytes,
                ForkId = forkId
            }
        );
    }

    /// <summary>
    /// Try to find fork by name, and return its cached server manifest data as stream, if it exists.
    /// Returns null if fails to find fork.
    /// </summary>
    public Stream? FindManifestCache(string forkName)
    {
        var rowId = Connection.QuerySingleOrDefault<long>(
            "SELECT ROWID FROM Fork WHERE Name == @Fork AND ServerManifestCache IS NOT NULL",
            new { Fork = forkName }
        );

        if (rowId == 0)
            return null;

        return SqliteBlobStream.Open(Connection.Handle!, "main", "Fork", "ServerManifestCache", rowId, false);
    }

    private static (string name, byte[] hash, long size) GetFileNameSha256Pair(string diskPath)
    {
        using var file = File.OpenRead(diskPath);

        return (Path.GetFileName(diskPath), SHA256.HashData(file), file.Length);
    }

    private sealed class ManifestData
    {
        public required IReadOnlyDictionary<string, ManifestBuildData> Builds { get; set; }
    }
}
