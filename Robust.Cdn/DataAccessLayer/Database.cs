using Dapper;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.DataAccessLayer.Models;
using Robust.Cdn.DataAccessLayer.Sqlite.Blob;
using Robust.Cdn.DataAccessLayer.Sqlite.Commands;

namespace Robust.Cdn.DataAccessLayer;

/// <summary>
/// Database service for CDN functionality.
/// </summary>
public sealed class Database(IOptions<CdnOptions> options) : ScopedSqliteRepositoryBase(options.Value)
{
    // Cached blob handle for efficient BLOB writes during ingest.
    // Reused across multiple row writes via Reopen() to avoid open/close overhead.
    private SqliteBlobStream? _contentBlob;

    // Cached blob handle for efficient BLOB reads during download.
    // Reused across multiple row reads via Reopen().
    private SqliteBlobStream? _readBlob;

    /// <summary> Count existing content versions. </summary>
    public int VersionCount()
    {
        return Connection.QuerySingleOrDefault<int>("SELECT COUNT(Id) FROM ContentVersion");
    }

    /// <summary>
    /// Ensures that fork record is created in cdn database.
    /// </summary>
    public int EnsureForkCreated(string forkName)
    {
        var id = Connection.QuerySingleOrDefault<int?>(
            "SELECT Id FROM Fork WHERE Name = @Name",
            new { Name = forkName });

        id ??= Connection.QuerySingle<int>(
            "INSERT INTO Fork (Name) VALUES (@Name) RETURNING Id",
            new { Name = forkName });

        return id.Value;
    }

    /// <summary>
    /// Get distinct blob count and manifest entries count for specified fork version.
    /// </summary>
    /// <param name="forkName">Fork name.</param>
    /// <param name="versionNumber">Human-readable build version number.</param>
    /// <returns>Null if no records with provided fork-version found, aggregate counts otherwise.</returns>
    public (long versionId, int countDistinctBlobs, int entriesCount)? GetDistinctBlobsAndManifestEntriesCounts(string forkName, string versionNumber)
    {
        var (versionId, countDistinctBlobs) = Connection.QuerySingleOrDefault<(long, int)>(
            """
            SELECT CV.Id, CV.CountDistinctBlobs
            FROM ContentVersion CV
            INNER JOIN main.Fork F on F.Id = CV.ForkId
            WHERE F.Name = @Fork AND Version = @Version
            """,
            new
            {
                Fork = forkName,
                Version = versionNumber
            });

        if (versionId == 0)
            return null;

        var entriesCount = Connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM ContentManifestEntry WHERE VersionId = @VersionId",
            new { VersionId = versionId }
        );

        return (versionId, countDistinctBlobs, entriesCount);
    }

    /// <summary>
    /// Updates counter for distinct blobs of content version for specified version.
    /// </summary>
    public void RefreshCountDistinctBlobs(long versionId)
    {
        Connection.Execute(
            """
            UPDATE ContentVersion AS cv
            SET CountDistinctBlobs =
                    (SELECT COUNT(DISTINCT cme.ContentId)
                    FROM ContentManifestEntry cme
                    WHERE cme.VersionId = cv.Id)
            WHERE cv.Id = @VersionId
            """,
            new { VersionId = versionId }
        );
    }

    /// <summary>
    /// Update content version data.
    /// </summary>
    public void UpdateContentVersionData(long versionId, byte[] manifestHash, int compressedLength)
    {
        Connection.Execute(
            """
            UPDATE ContentVersion
            SET ManifestHash = @ManifestHash, ManifestData = zeroblob(@ManifestDataSize)
            WHERE Id = @VersionId
            """,
            new
            {
                VersionId = versionId,
                ManifestHash = manifestHash,
                ManifestDataSize = compressedLength
            }
        );
    }

    /// <summary>
    /// Insert content version data and get generated id.
    /// </summary>
    /// <param name="forkId">Id of fork.</param>
    /// <param name="versionNumber">Human-readable build version number.</param>
    /// <returns>Id of newly created content version record.</returns>
    public long InsertContentVersionAndGetId(int forkId, string versionNumber)
    {
        return Connection.ExecuteScalar<long>(
            """
            INSERT INTO ContentVersion (ForkId, Version, TimeAdded, ManifestHash, ManifestData, CountDistinctBlobs)
            VALUES (@ForkId, @Version, datetime('now'), zeroblob(0), zeroblob(0), 0)
            RETURNING Id
            """,
            new { Version = versionNumber, ForkId = forkId }
        );
    }

    #region Working with blobs in database

    /// <summary>
    /// Write data into the Content.Data BLOB-typed column for a given content row.
    /// </summary>
    /// <remarks>
    /// Internally caches and reuses the SqliteBlobStream handle across calls via Reopen().
    /// </remarks>
    public void WriteContentBlob(long contentId, ReadOnlySpan<byte> data)
    {
        if (_contentBlob == null)
        {
            _contentBlob = SqliteBlobStream.Open(Connection.Handle!, "main", "Content", "Data", contentId, true);
        }
        else
        {
            _contentBlob.Reopen(contentId);
        }

        _contentBlob.Write(data);
    }

    /// <summary>
    /// Write compressed manifest data into the ContentVersion.ManifestData BLOB-typed column.
    /// </summary>
    public void WriteManifestBlob(long versionId, ReadOnlySpan<byte> data)
    {
        using var manifestBlob = SqliteBlobStream.Open(
            Connection.Handle!, "main", "ContentVersion", "ManifestData", versionId, true);

        manifestBlob.Write(data);
    }

    /// <summary>
    /// Open a read-only stream for content data on the given row.
    /// Internally caches and reuses the SqliteBlobStream handle across calls via Reopen().
    /// </summary>
    /// <param name="rowId">The id in Content table.</param>
    /// <returns>A read-only <see cref="Stream"/> backed by a <see cref="SqliteBlobStream"/>.</returns>
    public Stream OpenContentBlobForRead(long rowId)
    {
        if (_readBlob == null)
        {
            _readBlob = SqliteBlobStream.Open(
                Connection.Handle!, "main", "Content", "Data", rowId, canWrite: false);
        }
        else
        {
            _readBlob.Reopen(rowId);
        }

        return _readBlob;
    }

    /// <summary>
    /// Attempts to find Content version for provided version number, then returns stream of its ManifestData column.
    /// </summary>
    /// <param name="forkName">Fork name.</param>
    /// <param name="versionNumber">Human-readable build version number.</param>
    /// <returns>Stream with ManifestData and hash, null if no row with provided version number exists.</returns>
    public ManifestBlob? FindManifestDataBlob(string forkName, string versionNumber)
    {
        if (Transaction == null)
        {
            StartTransaction();
        }

        var (row, manifestHash) = Connection.QuerySingleOrDefault<(long, byte[])>(
            """
            SELECT CV.Id, CV.ManifestHash
            FROM ContentVersion CV
            INNER JOIN main.Fork F on F.Id = CV.ForkId
            WHERE F.Name = @Fork AND Version = @Version
            """,
            new
            {
                Fork = forkName,
                Version = versionNumber
            });

        if (row == 0)
            return null;

        var blob = SqliteBlobStream.Open(Connection.Handle!, "main", "ContentVersion", "ManifestData", row, false);
        return new(blob, Convert.ToHexString(manifestHash));
    }

    /// <summary>
    /// Release the cached content blob handle.
    /// </summary>
    public void ReleaseContentBlob()
    {
        _contentBlob?.Dispose();
        _contentBlob = null;
    }

    /// <summary>
    /// Release the cached read blob handle.
    /// </summary>
    public void ReleaseReadBlob()
    {
        _readBlob?.Dispose();
        _readBlob = null;
    }

    #endregion

    #region sqlite wrapped prepared commands

    public void InsertContentManifestEntry(long versionId, int idx, long contentId)
    {
        var command = GetPreparedCommand(() => new InsertContentManifestEntryCommand(Connection));
        command.Execute(versionId, idx, contentId);
    }

    /// <summary> Checks if ContentVersion exists. </summary>
    /// <param name="versionNumber">Human-readable build version number.</param>
    public bool IsVersionExisting(string versionNumber)
    {
        var command = GetPreparedCommand(() => new IsVersionExistingCheckCommand(Connection));
        return command.Execute(versionNumber);
    }

    /// <summary> Try to find content record by provided hash. </summary>
    public long? FindContentByHash(byte[] hash)
    {
        var command = GetPreparedCommand(() => new FindContentByHashCommand(Connection));
        return command.Execute(hash);
    }

    public (ContentCompression Compression, int Size, long RowId) ListContentMetadata(long versionId, int index)
    {
        var command = GetPreparedCommand(() => new ListContentMetadataCommand(Connection));
        return command.Execute(versionId, index);
    }

    public long InsertContent(byte[] hash, int dataLength, ContentCompression compression, int writeDataLength)
    {
        var command = GetPreparedCommand(()=> new InsertContentSqlitePreparedCommand(Connection));
        return command.Execute(hash, dataLength, compression, writeDataLength);
    }

    #endregion

    public override void Dispose()
    {
        ReleaseContentBlob();
        ReleaseReadBlob();
        base.Dispose();
    }

    private T GetPreparedCommand<T>(Func<T> factory) where T : SqlitePreparedCommandWrapperBase
    {
        var command = PreparedCommands.GetOrAdd(typeof(T),
            _ => factory());
        return (T)command;
    }
}
