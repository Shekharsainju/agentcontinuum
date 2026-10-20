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
}
