using System.Text;
using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Recovery;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.Recovery;

public sealed class RecoveryServiceTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;
    private readonly TaskRepository _tasks;
    private readonly LaunchLedger _ledger;
    private readonly FakeLister _lister = new();
    private readonly RecordingUsageLimits _usageLimits = new();
    private readonly TaskRecord _task = TestTasks.Sample("t1");

    public RecoveryServiceTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
        _tasks = new TaskRepository(_store);
        _ledger = new LaunchLedger(_store);
        _tasks.Add(_task);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private RecoveryService Service() =>
        new(_store, _ledger, _tasks, _lister, new OutcomeApplier(_tasks, _usageLimits));

    [Fact]
    public void Intent_NoProcess_Abandoned_TaskWaiting()
    {
        var opId = _ledger.BeginIntent("t1");
        _lister.Processes.Add(new ProcessInfo(100, 1, "claude.exe -p --session-id 11111111-2222-3333-4444-555555555555"));

        Service().Reconcile();

        Assert.Equal(OpState.Abandoned, _ledger.Get(opId)!.State);
        var task = _tasks.Get("t1")!;
        Assert.Equal(TaskState.WaitingForReset, task.State);
        Assert.Equal(1, task.AttemptsUsed);
    }

    [Theory]
    [InlineData("claude.exe -p --session-id {0} --output-format stream-json")]
    [InlineData("claude.exe -p --resume {0}")]
    [InlineData("CLAUDE.EXE --RESUME {1}")]
    public void Intent_ProcessWithSessionId_Interrupted_NeedsAttention(string commandLine)
    {
        var opId = _ledger.BeginIntent("t1");
        var session = _task.SessionId.ToString("D");
        _lister.Processes.Add(new ProcessInfo(100, 1, string.Format(commandLine, session, session.ToUpperInvariant())));

        Service().Reconcile();

        Assert.Equal(OpState.Interrupted, _ledger.Get(opId)!.State);
        var task = _tasks.Get("t1")!;
        Assert.Equal((TaskState.NeedsAttention, "Interrupted launch"), (task.State, task.AttentionReason));
        Assert.Equal(1, task.AttemptsUsed);
    }

    [Fact]
    public void Spawned_Interrupted_NeedsAttention()
    {
        var opId = _ledger.BeginIntent("t1");
        _ledger.MarkSpawned(opId, 100, 1, _task.SessionId);

        Service().Reconcile();

        Assert.Equal(OpState.Interrupted, _ledger.Get(opId)!.State);
        var task = _tasks.Get("t1")!;
        Assert.Equal((TaskState.NeedsAttention, "Interrupted run"), (task.State, task.AttentionReason));
        Assert.Equal(1, task.AttemptsUsed);
    }

    private string ExitedOp(int exitCode)
    {
        var opId = _ledger.BeginIntent("t1");
        _ledger.MarkSpawned(opId, 100, 1, _task.SessionId);
        _ledger.MarkExited(opId, exitCode);
        return opId;
    }

    private static FilteredEvent Event(string type, string? subtype, Dictionary<string, string> fields) =>
        new(type, subtype, null, null, null, fields);

    [Fact]
    public void Exited_ClassifiedFromRecordedEvents()
    {
        var opId = ExitedOp(0);
        StreamEventLog.Append(_store, opId, Event("system", "init", []));
        StreamEventLog.Append(_store, opId, Event("result", "success", new() { ["is_error"] = "false", ["terminal_reason"] = "\"completed\"" }));

        Service().Reconcile();

        var op = _ledger.Get(opId)!;
        Assert.Equal((OpState.Classified, Outcome.Completed), (op.State, op.Outcome));
        Assert.Equal(TaskState.Completed, _tasks.Get("t1")!.State);
    }

    [Fact]
    public void Exited_NoEvents_Failed()
    {
        var opId = ExitedOp(1);

        Service().Reconcile();

        var op = _ledger.Get(opId)!;
        Assert.Equal((OpState.Classified, Outcome.Failed, "Run failed"), (op.State, op.Outcome, op.Reason));
        var task = _tasks.Get("t1")!;
        Assert.Equal((TaskState.NeedsAttention, "Run failed"), (task.State, task.AttentionReason));
    }

    [Fact]
    public void Exited_VerifiedLimitHook_HandsResetToScheduler()
    {
        var rules = VersionRules.LoadEmbedded().Entries[0] with
        {
            Limit = new LimitSignalRule("StopFailure", "error", ["rate_limit"], "resets_at", JsonLiteral.UnixSeconds),
        };
        var opId = ExitedOp(1);
        var payload = $$"""{"hook_event_name":"StopFailure","session_id":"{{_task.SessionId}}","error":"rate_limit","resets_at":1800000000}""";
        HookRecorder.Record(_store, opId, new MemoryStream(Encoding.UTF8.GetBytes(payload)), rules);
        var service = new RecoveryService(_store, _ledger, _tasks, _lister, new OutcomeApplier(_tasks, _usageLimits), _ => rules);

        service.Reconcile();

        Assert.Equal(Outcome.UsageLimit, _ledger.Get(opId)!.Outcome);
        Assert.Equal([("t1", (long?)1_800_000_000)], _usageLimits.Calls);
    }

    [Fact]
    public void ListFails_NeedsAttention()
    {
        var intent = _ledger.BeginIntent("t1");
        _tasks.Add(TestTasks.Sample("t2"));
        var spawned = _ledger.BeginIntent("t2");
        _ledger.MarkSpawned(spawned, 100, 1, Guid.NewGuid());
        _lister.Fail = true;

        Service().Reconcile();

        Assert.Equal(OpState.Intent, _ledger.Get(intent)!.State);
        var task = _tasks.Get("t1")!;
        Assert.Equal((TaskState.NeedsAttention, "Process check failed"), (task.State, task.AttentionReason));
        Assert.Equal(OpState.Interrupted, _ledger.Get(spawned)!.State);
    }

    private (int Ops, int Live, int Attempts) Counts()
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT count(*) FROM launch_ops),
                (SELECT count(*) FROM launch_ops WHERE state IN ('INTENT','SPAWNED','EXITED')),
                (SELECT sum(attempts_used) FROM tasks)
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    [Fact]
    public void Reconcile_NeverLaunches()
    {
        _ledger.BeginIntent("t1");
        _tasks.Add(TestTasks.Sample("t2"));
        _ledger.MarkSpawned(_ledger.BeginIntent("t2"), 100, 1, Guid.NewGuid());
        _tasks.Add(TestTasks.Sample("t3"));
        var exited = _ledger.BeginIntent("t3");
        _ledger.MarkSpawned(exited, 101, 1, Guid.NewGuid());
        _ledger.MarkExited(exited, 0);
        var before = Counts();

        Service().Reconcile();
        Service().Reconcile();

        Assert.Equal((before.Ops, 0, before.Attempts), Counts());
        Assert.All(_tasks.List(), t => Assert.NotEqual(TaskState.Running, t.State));
    }

    private sealed class FakeLister : IProcessLister
    {
        public List<ProcessInfo> Processes { get; } = [];

        public bool Fail { get; set; }

        public int Calls { get; private set; }

        public IReadOnlyList<ProcessInfo> ListOwnProcesses()
        {
            Calls++;
            return Fail ? throw new ProcessListException("enumeration failed") : Processes;
        }
    }
}
