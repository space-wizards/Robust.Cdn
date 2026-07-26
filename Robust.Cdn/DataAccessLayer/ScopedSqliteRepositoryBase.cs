using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.Sqlite;
using Robust.Cdn.Config;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Robust.Cdn.DataAccessLayer.Sqlite.Commands;

namespace Robust.Cdn.DataAccessLayer;

/// <summary>
/// Base type for sqlite repository, scoped to a single request.
/// </summary>
public abstract class ScopedSqliteRepositoryBase(IDatabaseOptions options) : IDisposable
{
    private SqliteConnection? _connection;
    protected DbTransaction? Transaction;

    // cache for pre-compiled sqlite commands, to avoid re-preparing them on every request.
    protected readonly ConcurrentDictionary<Type, SqlitePreparedCommandWrapperBase> PreparedCommands = new();

    private readonly string _fileName = options.DatabaseFileName;

    protected SqliteConnection Connection => _connection ??= OpenConnection();

    private SqliteConnection OpenConnection()
    {
        var con = new SqliteConnection(GetConnectionString());
        con.Open();
        con.Execute("PRAGMA journal_mode=WAL");
        return con;
    }

    // deferred transaction is sqlite-specific, so we should change it to proper async when moving to Postgres
    [MemberNotNull(nameof(Transaction))]
    public void StartTransaction(bool deferred = false)
    {
        Transaction = Connection.BeginTransaction(deferred);
    }

    // sqlite does not support child transactions, so no point in tracking multiple transactions.
    public void Commit()
    {
        if (Transaction == null)
        {
            return;
        }

        Transaction.Commit();
        Transaction = null;
    }

#pragma warning disable CA1816
    public virtual void Dispose()
    {
        Transaction?.Rollback();
        foreach (var wrapper in PreparedCommands)
        {
            wrapper.Value.Dispose();
        }

        _connection?.Dispose();
    }
#pragma warning restore CA1816

    private string GetConnectionString()
        => $"Data Source={_fileName};Mode=ReadWriteCreate;Pooling=True;Foreign Keys=True";
}
