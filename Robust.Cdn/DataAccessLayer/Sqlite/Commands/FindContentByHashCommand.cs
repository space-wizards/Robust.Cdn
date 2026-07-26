using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Robust.Cdn.DataAccessLayer.Sqlite.Commands;

public class FindContentByHashCommand(SqliteConnection connection)
    : SqlitePreparedCommandWrapperBase(connection, "SELECT Id FROM Content WHERE Hash = ?")
{
    public long? Execute(byte[] hash)
    {
        Reset();
        BindBlob(1, hash);
        if (Step() == raw.SQLITE_DONE)
            return null;

        return ColumnInt64(0);
    }
}
