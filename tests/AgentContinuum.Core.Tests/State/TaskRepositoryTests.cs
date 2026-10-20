using AgentContinuum.Core.Model;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.State;

public sealed class TaskRepositoryTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;
    private readonly TaskRepository _tasks;

    public TaskRepositoryTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
        _tasks = new TaskRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Add_GetReturnsSameRecord()
    {
        var task = TestTasks.Sample("t1");

        _tasks.Add(task);

        Assert.Equivalent(task, _tasks.Get("t1"), strict: true);
    }

    [Fact]
    public void Get_UnknownTask_ReturnsNull()
    {
        Assert.Null(_tasks.Get("missing"));
    }

    [Fact]
    public void List_ReturnsTasksInAddOrder()
    {
        _tasks.Add(TestTasks.Sample("t2"));
        _tasks.Add(TestTasks.Sample("t1"));

        Assert.Equal(["t2", "t1"], _tasks.List().Select(t => t.TaskId));
    }

    [Fact]
    public void SetState_StoresStateAndReason()
    {
        _tasks.Add(TestTasks.Sample("t1"));

        _tasks.SetState("t1", TaskState.NeedsAttention, "Run failed");

        var task = _tasks.Get("t1")!;
        Assert.Equal(TaskState.NeedsAttention, task.State);
        Assert.Equal("Run failed", task.AttentionReason);
    }

    [Fact]
    public void SetState_NullReason_ClearsReason()
    {
        _tasks.Add(TestTasks.Sample("t1") with { State = TaskState.NeedsAttention, AttentionReason = "Run failed" });

        _tasks.SetState("t1", TaskState.WaitingForReset, null);

        Assert.Null(_tasks.Get("t1")!.AttentionReason);
    }

    [Fact]
    public void SetState_UnknownTask_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => _tasks.SetState("missing", TaskState.Revoked, null));
    }
}
