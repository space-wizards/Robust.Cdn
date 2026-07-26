using Microsoft.Data.Sqlite;

namespace Robust.Cdn.DataAccessLayer.Sqlite.Commands;

public class InsertContentManifestEntryCommand(SqliteConnection connection) : SqlitePreparedCommandWrapperBase(
    connection,
    """
    INSERT INTO ContentManifestEntry (VersionId, ManifestIdx, ContentId)
    VALUES (@VersionId, @ManifestIdx, @ContentId)
    """
)
{
    public void Execute(long versionId, int idx, long contentId)
    {
        BindInt64(1, versionId);
        BindInt64(2, idx); // @ManifestIdx
        BindInt64(3, contentId); // @ContentId

        Step();
        Reset();
    }
}
