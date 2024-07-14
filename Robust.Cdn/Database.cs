using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;

namespace Robust.Cdn;

public abstract class BaseScopedDatabase : IDisposable
{
    private SqliteConnection? _connection;
    public SqliteConnection Connection => _connection ??= OpenConnection();

    private SqliteConnection OpenConnection()
    {
        var con = new SqliteConnection(GetConnectionString());
        con.Open();
        con.Execute("PRAGMA journal_mode=WAL");
        return con;
    }

#pragma warning disable CA1816
    public void Dispose()
    {
        _connection?.Dispose();
    }
#pragma warning restore CA1816

    protected abstract string GetConnectionString();

    protected string GetConnectionStringForFile(string fileName)
    {
        return $"Data Source={fileName};Mode=ReadWriteCreate;Pooling=True;Foreign Keys=True";
    }
}

/// <summary>
/// Database service for CDN functionality.
/// </summary>
public sealed class Database(IOptions<CdnOptions> options) : BaseScopedDatabase
{
    protected override string GetConnectionString()
    {
        return GetConnectionStringForFile(options.Value.DatabaseFileName);
    }
}

/// <summary>
/// Database service for server manifest functionality.
/// </summary>
public sealed class ManifestDatabase(IOptions<ManifestOptions> options) : BaseScopedDatabase
{
    protected override string GetConnectionString()
    {
        return GetConnectionStringForFile(options.Value.DatabaseFileName);
    }

    public void EnsureForksCreated()
    {
        var con = Connection;
        using var tx = con.BeginTransaction();

        foreach (var forkName in options.Value.Forks.Keys)
        {
            con.Execute("INSERT INTO Fork (Name) VALUES (@Name) ON CONFLICT DO NOTHING", new { Name = forkName });
        }

        tx.Commit();
    }
}
