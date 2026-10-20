using Microsoft.Data.Sqlite;

namespace AgentContinuum.Core.State;

public sealed class StateStore : IDisposable
{
    public const int CurrentSchemaVersion = 1;

    private const string SchemaV1 = """
        CREATE TABLE tasks (
            task_id TEXT PRIMARY KEY,
            label TEXT NOT NULL,
            worktree TEXT NOT NULL,
            prompt_file TEXT NOT NULL,
            permission_mode TEXT NOT NULL,
            allowed_tools_json TEXT NOT NULL,
            max_attempts INTEGER NOT NULL,
            deadline_utc INTEGER NOT NULL,
            session_id TEXT NOT NULL,
            cli_path TEXT NOT NULL,
            cli_sha256 TEXT NOT NULL,
            cli_version TEXT NOT NULL,
            state TEXT NOT NULL,
            attempts_used INTEGER NOT NULL DEFAULT 0,
            reset_expected_at INTEGER,
            attempt_permitted_at INTEGER,
            baseline_fingerprint TEXT,
            attention_reason TEXT
        );
        CREATE TABLE launch_ops (
            op_id TEXT PRIMARY KEY,
            task_id TEXT NOT NULL,
            window_id INTEGER NOT NULL,
            attempt_no INTEGER NOT NULL,
            state TEXT NOT NULL,
            pid INTEGER,
            proc_created_at INTEGER,
            session_id TEXT,
            exit_code INTEGER,
            outcome TEXT,
            reason TEXT,
            created_utc INTEGER NOT NULL
        );
        CREATE TABLE events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            op_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            fields_json TEXT NOT NULL,
            at_utc INTEGER NOT NULL
        );
        CREATE INDEX events_by_op ON events(op_id, id);
        CREATE TABLE requests (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL,
            task_id TEXT,
            args_json TEXT NOT NULL,
            created_utc INTEGER NOT NULL,
            status TEXT NOT NULL,
            result_json TEXT
        );
        CREATE TABLE attestations (
            kind TEXT NOT NULL,
            at_utc INTEGER NOT NULL
        );
        CREATE TABLE meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    private StateStore(SqliteConnection connection)
    {
        Connection = connection;
    }

    public SqliteConnection Connection { get; }

    public int SchemaVersion
    {
        get
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
            return cmd.ExecuteScalar() is string value && int.TryParse(value, out var version) ? version : 0;
        }
    }

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
            store.EnsureSchema();
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public T InTransaction<T>(Func<SqliteTransaction, T> work)
    {
        using var tx = Connection.BeginTransaction();
        var result = work(tx);
        tx.Commit();
        return result;
    }

    public void Dispose() => Connection.Dispose();

    private void EnsureSchema()
    {
        InTransaction(tx =>
        {
            using var exists = Connection.CreateCommand();
            exists.Transaction = tx;
            exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'";
            if ((long)exists.ExecuteScalar()! > 0)
            {
                return false;
            }

            using var create = Connection.CreateCommand();
            create.Transaction = tx;
            create.CommandText = SchemaV1 + $"INSERT INTO meta (key, value) VALUES ('schema_version', '{CurrentSchemaVersion}');";
            create.ExecuteNonQuery();
            return true;
        });
    }

    private void Execute(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
