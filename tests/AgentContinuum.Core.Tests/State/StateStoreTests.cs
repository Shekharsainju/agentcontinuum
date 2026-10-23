using AgentContinuum.Core.State;
using Microsoft.Data.Sqlite;

namespace AgentContinuum.Core.Tests.State;

public sealed class StateStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private StateStore Open() => StateStore.Open(_dir.File(Path.Combine("state", "ac.db")));

    private static object? Scalar(StateStore store, string sql)
    {
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void InsertOp(StateStore store, string opId, string taskId, string state)
    {
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO launch_ops (op_id, task_id, window_id, attempt_no, state, created_utc)
            VALUES ($op, $task, 0, 1, $state, 0)
            """;
        cmd.Parameters.AddWithValue("$op", opId);
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("INTENT")]
    [InlineData("SPAWNED")]
    [InlineData("EXITED")]
    public void PartialIndex_RejectsSecondLiveOpForTask(string liveState)
    {
        using var store = Open();
        InsertOp(store, "op1", "task1", liveState);

        Assert.Throws<SqliteException>(() => InsertOp(store, "op2", "task1", "INTENT"));
    }

    [Theory]
    [InlineData("CLASSIFIED")]
    [InlineData("ABANDONED")]
    [InlineData("INTERRUPTED")]
    public void PartialIndex_AllowsNewOpAfterClassified(string finishedState)
    {
        using var store = Open();
        InsertOp(store, "op1", "task1", finishedState);

        InsertOp(store, "op2", "task1", "INTENT");

        Assert.Equal(2L, Scalar(store, "SELECT count(*) FROM launch_ops WHERE task_id = 'task1'"));
    }

    [Fact]
    public void IntegrityOk_TrueOnFreshDb()
    {
        using var store = Open();

        Assert.True(store.IntegrityOk());
    }

    [Fact]
    public void IntegrityOk_FalseOnCorruptPage()
    {
        var dbPath = _dir.File(Path.Combine("state", "ac.db"));
        using (var store = Open())
        {
            for (var i = 0; i < 200; i++)
            {
                InsertOp(store, $"op{i}", $"task{i}", "CLASSIFIED");
            }
        }
        var bytes = File.ReadAllBytes(dbPath);
        Assert.True(bytes.Length > 16384);
        Array.Fill(bytes, (byte)0xA5, 8192, 4096);
        File.WriteAllBytes(dbPath, bytes);

        using var reopened = Open();

        Assert.False(reopened.IntegrityOk());
    }

    [Fact]
    public void Open_SetsWalAndSynchronousFull()
    {
        using var store = Open();

        Assert.Equal("wal", Scalar(store, "PRAGMA journal_mode"));
        Assert.Equal(2L, Scalar(store, "PRAGMA synchronous"));
    }

    [Fact]
    public void Open_CreatesSchemaVersion1()
    {
        using var store = Open();

        Assert.Equal(1, store.SchemaVersion);
        foreach (var table in new[] { "tasks", "launch_ops", "events", "requests", "attestations", "meta" })
        {
            Assert.Equal(1L, Scalar(store, $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'"));
        }
    }

    [Fact]
    public void Open_ExistingStore_KeepsSchemaVersion1()
    {
        Open().Dispose();

        using var store = Open();

        Assert.Equal(1, store.SchemaVersion);
    }
}
