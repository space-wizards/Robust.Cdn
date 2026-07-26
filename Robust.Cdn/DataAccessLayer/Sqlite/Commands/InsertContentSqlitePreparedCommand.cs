using Microsoft.Data.Sqlite;

namespace Robust.Cdn.DataAccessLayer.Sqlite.Commands;

public class InsertContentSqlitePreparedCommand(SqliteConnection connection)
    : SqlitePreparedCommandWrapperBase(
        connection,
        """
        INSERT INTO Content (Hash, Size, Compression, Data)
        VALUES (@Hash, @Size, @Compression, @Data)
        RETURNING Id
        """
    )
{
    public long Execute(byte[] hash, int dataLength, ContentCompression compression, int contentLength)
    {
        BindBlob(1, hash); // @Hash
        BindInt(2, dataLength); // @Size
        BindInt(3, (int)compression); // @Compression
        BindZeroBlob(4, contentLength); // @Data

        Step();
        var contentId = ColumnInt64(0);

        Reset();
        return contentId;
    }
}
