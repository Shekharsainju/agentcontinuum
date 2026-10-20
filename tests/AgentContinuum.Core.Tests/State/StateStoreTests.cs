using AgentContinuum.Core.State;

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
