using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.Classify;

public sealed class OutcomeApplierTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;
    private readonly TaskRepository _tasks;
    private readonly RecordingUsageLimits _usageLimits = new();
    private readonly OutcomeApplier _applier;

    public OutcomeApplierTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
        _tasks = new TaskRepository(_store);
        _tasks.Add(TestTasks.Sample("t1", maxAttempts: 3) with { State = TaskState.Running, AttemptsUsed = 1 });
        _applier = new OutcomeApplier(_tasks, _usageLimits);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Completed_TaskCompleted()
    {
        _applier.Apply("t1", new Classification(Outcome.Completed, "Completed", null));

        Assert.Equal(TaskState.Completed, _tasks.Get("t1")!.State);
        Assert.Empty(_usageLimits.Calls);
    }

    [Fact]
    public void UsageLimit_HandsResetToScheduler()
    {
        _applier.Apply("t1", new Classification(Outcome.UsageLimit, "Usage limit", 1_800_000_000));

        Assert.Equal([("t1", (long?)1_800_000_000)], _usageLimits.Calls);
    }

    [Fact]
    public void UsageLimit_AttemptsExhausted_NeedsAttention()
    {
        _tasks.Add(TestTasks.Sample("t2", maxAttempts: 2) with { State = TaskState.Running, AttemptsUsed = 2 });

        _applier.Apply("t2", new Classification(Outcome.UsageLimit, "Usage limit", 1_800_000_000));

        var task = _tasks.Get("t2")!;
        Assert.Equal((TaskState.NeedsAttention, "Attempts exhausted"), (task.State, task.AttentionReason));
        Assert.Empty(_usageLimits.Calls);
    }

    [Theory]
    [InlineData(Outcome.Failed, "Run failed")]
    [InlineData(Outcome.Ambiguous, "Stopped, reason unverified")]
    [InlineData(Outcome.BillingMismatch, "Billing unverified")]
    public void OtherOutcomes_NeedsAttentionWithReason(Outcome outcome, string reason)
    {
        _applier.Apply("t1", new Classification(outcome, reason, null));

        var task = _tasks.Get("t1")!;
        Assert.Equal((TaskState.NeedsAttention, reason), (task.State, task.AttentionReason));
        Assert.Empty(_usageLimits.Calls);
    }
}
