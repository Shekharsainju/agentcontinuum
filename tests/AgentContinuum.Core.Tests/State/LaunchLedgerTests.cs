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

    // Recovery reads a PID on an INTENT row as proof a process existed, so it is written before the process runs.
    [Fact]
    public void MarkCreated_RecordsProcessAndStaysIntent()
    {
        var ledger = new LaunchLedger(_store);
        var opId = ledger.BeginIntent("t1");

        ledger.MarkCreated(opId, 4242, 133_000_000_000_000_000);

        var op = ledger.Get(opId)!;
        Assert.Equal((OpState.Intent, 4242, 133_000_000_000_000_000L), (op.State, op.Pid, op.ProcCreatedAtFileTime));
        Assert.Equal(opId, ledger.Live("t1")!.OpId);
    }

    [Fact]
    public void MarkCreated_OnlyOnIntent()
    {
        var ledger = new LaunchLedger(_store);
        var opId = ledger.BeginIntent("t1");
        ledger.Abandon(opId);

        Assert.Throws<InvalidOperationException>(() => ledger.MarkCreated(opId, 4242, 133_000_000_000_000_000));
        Assert.Null(ledger.Get(opId)!.Pid);
    }

    [Fact]
    public void BeginIntent_CountsAttemptInSameTransaction()
    {
        var ledger = new LaunchLedger(_store, checkpoint: point =>
        {
            if (point == LaunchLedger.IntentInserted)
            {
                throw new IOException("injected");
            }
        });

        Assert.Throws<IOException>(() => ledger.BeginIntent("t1"));

        var task = _tasks.Get("t1")!;
        Assert.Equal(0, task.AttemptsUsed);
        Assert.Equal(TaskState.Queued, task.State);
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM launch_ops";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public void BeginIntent_SecondLiveIntentThrows()
    {
        var ledger = new LaunchLedger(_store);
        ledger.BeginIntent("t1");

        Assert.Throws<InvalidOperationException>(() => ledger.BeginIntent("t1"));

        Assert.Equal(1, _tasks.Get("t1")!.AttemptsUsed);
    }

    [Fact]
    public void BeginIntent_AttemptsExhaustedThrows()
    {
        _tasks.Add(TestTasks.Sample("t2", maxAttempts: 2) with { AttemptsUsed = 2 });
        var ledger = new LaunchLedger(_store);

        Assert.Throws<InvalidOperationException>(() => ledger.BeginIntent("t2"));

        Assert.Equal(2, _tasks.Get("t2")!.AttemptsUsed);
        Assert.Null(ledger.Live("t2"));
    }

    private static readonly (OpState From, OpState To)[] Legal =
    [
        (OpState.Intent, OpState.Spawned),
        (OpState.Intent, OpState.Abandoned),
        (OpState.Intent, OpState.Interrupted),
        (OpState.Spawned, OpState.Exited),
        (OpState.Spawned, OpState.Interrupted),
        (OpState.Exited, OpState.Classified),
    ];

    public static TheoryData<OpState, OpState> LegalPairs()
    {
        var data = new TheoryData<OpState, OpState>();
        foreach (var (from, to) in Legal)
        {
            data.Add(from, to);
        }
        return data;
    }

    public static TheoryData<OpState, OpState> IllegalPairs()
    {
        var data = new TheoryData<OpState, OpState>();
        foreach (var from in Enum.GetValues<OpState>())
        {
            foreach (var to in Enum.GetValues<OpState>().Where(s => s != OpState.Intent))
            {
                if (!Legal.Contains((from, to)))
                {
                    data.Add(from, to);
                }
            }
        }
        return data;
    }

    private static readonly Guid Session = Guid.Parse("6f9c1f3e-2b7a-4c55-9d0e-1a2b3c4d5e6f");

    private static string DriveTo(LaunchLedger ledger, string taskId, OpState state)
    {
        var opId = ledger.BeginIntent(taskId);
        switch (state)
        {
            case OpState.Spawned:
                Apply(ledger, opId, OpState.Spawned);
                break;
            case OpState.Exited:
                Apply(ledger, opId, OpState.Spawned);
                Apply(ledger, opId, OpState.Exited);
                break;
            case OpState.Classified:
                Apply(ledger, opId, OpState.Spawned);
                Apply(ledger, opId, OpState.Exited);
                Apply(ledger, opId, OpState.Classified);
                break;
            case OpState.Abandoned:
            case OpState.Interrupted:
                Apply(ledger, opId, state);
                break;
        }
        return opId;
    }

    private static void Apply(LaunchLedger ledger, string opId, OpState to)
    {
        switch (to)
        {
            case OpState.Spawned:
                ledger.MarkSpawned(opId, 4242, 133_000_000_000_000_000, Session);
                break;
            case OpState.Exited:
                ledger.MarkExited(opId, 7);
                break;
            case OpState.Classified:
                ledger.Classify(opId, Outcome.Failed, "Run failed");
                break;
            case OpState.Abandoned:
                ledger.Abandon(opId);
                break;
            case OpState.Interrupted:
                ledger.Interrupt(opId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(to));
        }
    }

    [Theory]
    [MemberData(nameof(LegalPairs))]
    public void Transitions_FollowStateMachine(OpState from, OpState to)
    {
        var ledger = new LaunchLedger(_store);
        var opId = DriveTo(ledger, "t1", from);

        Apply(ledger, opId, to);

        Assert.Equal(to, ledger.Get(opId)!.State);
    }

    [Theory]
    [MemberData(nameof(IllegalPairs))]
    public void Transitions_IllegalPairThrows(OpState from, OpState to)
    {
        var ledger = new LaunchLedger(_store);
        var opId = DriveTo(ledger, "t1", from);

        Assert.Throws<InvalidOperationException>(() => Apply(ledger, opId, to));

        Assert.Equal(from, ledger.Get(opId)!.State);
    }

    [Fact]
    public void Transitions_UnknownOpThrows()
    {
        var ledger = new LaunchLedger(_store);

        Assert.Throws<InvalidOperationException>(() => ledger.Abandon("missing"));
    }

    [Fact]
    public void Transitions_RecordProcessExitAndOutcome()
    {
        var ledger = new LaunchLedger(_store);

        var opId = DriveTo(ledger, "t1", OpState.Classified);

        var op = ledger.Get(opId)!;
        Assert.Equal(4242, op.Pid);
        Assert.Equal(133_000_000_000_000_000, op.ProcCreatedAtFileTime);
        Assert.Equal(Session, op.SessionId);
        Assert.Equal(7, op.ExitCode);
        Assert.Equal(Outcome.Failed, op.Outcome);
        Assert.Equal("Run failed", op.Reason);
    }

    [Fact]
    public void Classify_FreesTaskForNextIntent()
    {
        var ledger = new LaunchLedger(_store);
        var first = DriveTo(ledger, "t1", OpState.Classified);
        // The outcome is applied to the task before the op is classified.
        _tasks.SetState("t1", TaskState.WaitingForReset, null);

        var second = ledger.BeginIntent("t1");

        Assert.NotEqual(first, second);
        Assert.Equal(second, ledger.Live("t1")!.OpId);
        Assert.Equal(2, ledger.Get(second)!.AttemptNo);
    }

    private void Finish(LaunchLedger ledger, string opId, Outcome outcome)
    {
        ledger.MarkSpawned(opId, 4242, 133_000_000_000_000_000, Session);
        ledger.MarkExited(opId, 1);
        _tasks.SetState("t1", TaskState.WaitingForReset, null);
        ledger.Classify(opId, outcome, "test");
    }

    [Fact]
    public void BeginIntent_WindowIdCountsPriorUsageLimits()
    {
        var ledger = new LaunchLedger(_store);
        Finish(ledger, ledger.BeginIntent("t1"), Outcome.UsageLimit);
        Finish(ledger, ledger.BeginIntent("t1"), Outcome.Failed);

        var third = ledger.BeginIntent("t1");

        Assert.Equal(1, ledger.Get(third)!.WindowId);
        Assert.Equal(3, ledger.Get(third)!.AttemptNo);
    }
}
