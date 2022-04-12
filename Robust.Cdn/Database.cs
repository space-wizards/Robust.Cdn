using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Robust.Cdn;

public sealed class Database : IDisposable
{
    private readonly IOptions<CdnOptions> _options;
    private SqliteConnection? _connection;
    public SqliteConnection Connection => _connection ??= OpenConnection();

    public Database(IOptions<CdnOptions> options)
    {
        _options = options;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    private SqliteConnection OpenConnection()
    {
        var options = _options.Value;
        var conString = $"Data Source={options.DatabaseFileName};Mode=ReadWriteCreate;Pooling=True;Foreign Keys=True";

        var con = new SqliteConnection(conString);
        con.Open();
        return con;
    }
}
