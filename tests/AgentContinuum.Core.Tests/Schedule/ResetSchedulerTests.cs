using AgentContinuum.Core.Model;
using AgentContinuum.Core.Schedule;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.Schedule;

public sealed class ResetSchedulerTests : IDisposable
{
    private const long Reset = 1_800_000_000;

    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;
    private readonly TaskRepository _tasks;
    private readonly FakeClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(Reset - 3600));
    private readonly RecordingLog _log = new();
    private readonly ResetScheduler _scheduler;

    public ResetSchedulerTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
        _tasks = new TaskRepository(_store);
        _tasks.Add(TestTasks.Sample("t1") with { State = TaskState.Running, AttemptsUsed = 1 });
        _scheduler = new ResetScheduler(_tasks, _clock, _log);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Permitted_IsResetPlus2Min()
    {
        _scheduler.OnUsageLimit("t1", Reset);

        var task = _tasks.Get("t1")!;
        Assert.Equal(TaskState.WaitingForReset, task.State);
        Assert.Equal(Reset, task.ResetExpectedAtEpoch);
        Assert.Equal(Reset + 120, task.AttemptPermittedAtEpoch);
    }

    [Fact]
    public void UnknownReset_PermittedNow()
    {
        _scheduler.OnUsageLimit("t1", null);

        var task = _tasks.Get("t1")!;
        Assert.Equal(TaskState.WaitingForReset, task.State);
        Assert.Null(task.ResetExpectedAtEpoch);
        Assert.Equal(_clock.UtcNow.ToUnixTimeSeconds(), task.AttemptPermittedAtEpoch);
    }
}
