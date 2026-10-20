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
}
