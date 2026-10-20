using Microsoft.Data.Sqlite;

namespace AgentContinuum.Core.State;

public sealed class StateStore : IDisposable
{
    private StateStore(SqliteConnection connection)
    {
        Connection = connection;
    }

    public SqliteConnection Connection { get; }

    public static StateStore Open(string dbPath)
    {
        var fullPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // Pooling off so Dispose releases the file handle.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var store = new StateStore(connection);
        try
        {
            store.Execute("PRAGMA journal_mode=WAL");
            store.Execute("PRAGMA synchronous=FULL");
            // The daemon, the hook and each command write from separate processes.
            store.Execute("PRAGMA busy_timeout=5000");
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public void Dispose() => Connection.Dispose();

    private void Execute(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
