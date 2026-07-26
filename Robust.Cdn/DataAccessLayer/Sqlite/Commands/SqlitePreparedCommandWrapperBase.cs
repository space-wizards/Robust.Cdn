using Microsoft.Data.Sqlite;
using SQLitePCL;
using static SQLitePCL.raw;

namespace Robust.Cdn.DataAccessLayer.Sqlite.Commands;

/// <summary>
/// Base type for prepared command wrappers that handle the lifecycle of a prepared sqlite
/// statement and provide helper methods for binding parameters and retrieving column values.
/// Works as optimization.
/// </summary>
public abstract class SqlitePreparedCommandWrapperBase : IDisposable
{
    protected readonly sqlite3_stmt Prepared;

    protected SqlitePreparedCommandWrapperBase(SqliteConnection connection, string commandText)
    {
        if (connection.Handle == null)
            throw new ArgumentException("Expected SqliteConnection to have properly initialized Handle but it is null");

        Prepared = Prepare(connection.Handle,commandText);
    }

    public void Dispose()
    {
        Prepared.Dispose();
    }

    /// <summary>
    /// Create sqlite prepared statement from command text. If same command was already processed - it will be reused.
    /// </summary>
    private static sqlite3_stmt Prepare(sqlite3 con, string command)
    {
        CheckErr(sqlite3_prepare_v2(con, command, out var stmt), con);

        return stmt;
    }

    /// <summary>
    /// Bind <seealso cref="Prepared"/> argument on index <seealso cref="Index"/> to provided value.
    /// </summary>
    protected void BindString(int index, ReadOnlySpan<char> data)
    {
        CheckErr(sqlite3_bind_text16(Prepared, index, data));
    }

    /// <summary>
    /// Bind <seealso cref="Prepared"/> argument on index <seealso cref="Index"/> to provided value.
    /// </summary>
    protected void BindBlob(int index, ReadOnlySpan<byte> data)
    {
        CheckErr(sqlite3_bind_blob(Prepared, index, data));
    }

    /// <summary>
    /// Bind <seealso cref="Prepared"/> argument on index <seealso cref="Index"/> to provided value.
    /// </summary>
    protected void BindInt(int index, int value)
    {
        CheckErr(sqlite3_bind_int(Prepared, index, value));
    }

    /// <summary>
    /// Bind <seealso cref="Prepared"/> argument on index <seealso cref="Index"/> to provided value.
    /// </summary>
    protected void BindInt64(int index, long value)
    {
        CheckErr(sqlite3_bind_int64(Prepared, index, value));
    }

    /// <summary>
    /// Bind <seealso cref="Prepared"/> argument on index <seealso cref="Index"/> to provided value.
    /// </summary>
    protected void BindZeroBlob(int index, int length)
    {
        CheckErr(sqlite3_bind_zeroblob(Prepared, index, length));
    }

    /// <summary>
    /// Extracts value from column number <see cref="index"/> response of command execution as long.
    /// </summary>
    protected long ColumnInt64(int index)
    {
        return sqlite3_column_int64(Prepared, index);
    }

    /// <summary>
    /// Extracts value from column number <see cref="index"/> response of command execution as int.
    /// </summary>
    protected int ColumnInt(int index)
    {
        return sqlite3_column_int(Prepared, index);
    }

    /// <summary>
    /// Attempts to execute one step of command (attempt to extract row).
    /// </summary>
    /// <returns></returns>
    protected int Step()
    {
        return CheckErr(sqlite3_step(Prepared));
    }

    /// <summary>
    /// Resets command to be reused without reconstruction. Drops results.
    /// </summary>
    protected void Reset()
    {
        CheckErr(sqlite3_reset(Prepared));
    }

    protected static int CheckErr(int err, sqlite3? db = null)
    {
        SqliteException.ThrowExceptionForRC(err, db);
        return err;
    }
}
