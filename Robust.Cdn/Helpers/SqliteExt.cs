using Microsoft.Data.Sqlite;
using SQLitePCL;
using static SQLitePCL.raw;

namespace Robust.Cdn.Helpers;

public static class SqliteExt
{
    public static sqlite3_stmt Prepare(this sqlite3 con, string command)
    {
        CheckErr(sqlite3_prepare_v2(con, command, out var stmt), con);

        return stmt;
    }

    public static void BindString(this sqlite3_stmt stmt, int index, ReadOnlySpan<char> data)
    {
        CheckErr(sqlite3_bind_text16(stmt, index, data));
    }

    public static void BindBlob(this sqlite3_stmt stmt, int index, ReadOnlySpan<byte> data)
    {
        CheckErr(sqlite3_bind_blob(stmt, index, data));
    }

    public static void BindInt(this sqlite3_stmt stmt, int index, int value)
    {
        CheckErr(sqlite3_bind_int(stmt, index, value));
    }

    public static void BindInt64(this sqlite3_stmt stmt, int index, long value)
    {
        CheckErr(sqlite3_bind_int64(stmt, index, value));
    }

    public static void BindZeroBlob(this sqlite3_stmt stmt, int index, int length)
    {
        CheckErr(sqlite3_bind_zeroblob(stmt, index, length));
    }

    public static long ColumnInt64(this sqlite3_stmt stmt, int index)
    {
        return sqlite3_column_int64(stmt, index);
    }

    public static int ColumnInt(this sqlite3_stmt stmt, int index)
    {
        return sqlite3_column_int(stmt, index);
    }

    public static int Step(this sqlite3_stmt stmt)
    {
        return CheckErr(sqlite3_step(stmt));
    }

    public static void Reset(this sqlite3_stmt stmt)
    {
        CheckErr(sqlite3_reset(stmt));
    }

    public static int CheckErr(int err, sqlite3? db = null)
    {
        SqliteException.ThrowExceptionForRC(err, db);
        return err;
    }
}
