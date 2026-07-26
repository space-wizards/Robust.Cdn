using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Robust.Cdn.DataAccessLayer.Sqlite.Commands;

public class ListContentMetadataCommand(SqliteConnection connection)
    : SqlitePreparedCommandWrapperBase(
        connection,
        """
        SELECT c.Compression, c.Size, c.Id
        FROM ContentManifestEntry cme
            INNER JOIN Content c on c.Id = cme.ContentId
        WHERE cme.VersionId = @VersionId AND cme.ManifestIdx = @ManifestIdx
        """
    )
{
    public (ContentCompression Compression, int Size, long RowId) Execute(long versionId, int index)
    {
        Reset();
        BindInt64(1, versionId);// @VersionId
        BindInt(2, index);
        if (Step() != raw.SQLITE_ROW)
        {
            throw new InvalidOperationException("Unable to find manifest row??");
        }
        var compression = (ContentCompression)ColumnInt(0);
        var size = ColumnInt(1);
        var rowId = ColumnInt64(2);

        return (compression, size, rowId);
    }
}
