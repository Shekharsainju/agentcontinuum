using AgentContinuum.Core.Model;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.State;

public sealed class LaunchLedgerTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;
    private readonly TaskRepository _tasks;

    public LaunchLedgerTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
        _tasks = new TaskRepository(_store);
        _tasks.Add(TestTasks.Sample("t1"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void BeginIntent_RecordsIntentAndCountsAttempt()
    {
        var ledger = new LaunchLedger(_store);

        var opId = ledger.BeginIntent("t1");

        var op = ledger.Get(opId)!;
        Assert.Equal(OpState.Intent, op.State);
        Assert.Equal("t1", op.TaskId);
        Assert.Equal(1, op.AttemptNo);
        Assert.Equal(0, op.WindowId);
        Assert.Null(op.Pid);
        var task = _tasks.Get("t1")!;
        Assert.Equal(1, task.AttemptsUsed);
        Assert.Equal(TaskState.Running, task.State);
    }
}
