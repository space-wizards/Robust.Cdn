using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Robust.Cdn.DataAccessLayer.Sqlite.Commands;

public class IsVersionExistingCheckCommand(SqliteConnection connection)
    : SqlitePreparedCommandWrapperBase(connection, "SELECT 1 FROM ContentVersion WHERE Version = ?")
{
    public bool Execute(string version)
    {
        Reset();
        BindString(1, version);

        if (Step() == raw.SQLITE_ROW)
            return true;

        return false;
    }
}
