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
        _lister.Processes.Add(new ProcessInfo(200, 1, @"C:\Windows\explorer.exe", "explorer.exe"));
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
        _lister.Processes.Add(new ProcessInfo(100, 1, _task.CliPath, "claude.exe -p --session-id 11111111-2222-3333-4444-555555555555"));

        Service().Reconcile();

        Assert.Equal(OpState.Abandoned, _ledger.Get(opId)!.State);
        var task = _tasks.Get("t1")!;
        Assert.Equal(TaskState.WaitingForReset, task.State);
        Assert.Equal(1, task.AttemptsUsed);
    }

    // Kill-on-close ended the process with the daemon, so no listing finds it, yet it ran and may have done work.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Intent_WithProcessRecorded_Interrupted_NeedsAttention(bool listingFails)
    {
        var opId = _ledger.BeginIntent("t1");
        _ledger.MarkCreated(opId, 4242, 133_000_000_000_000_000);
        _lister.Fail = listingFails;

        Service().Reconcile();

        Assert.Equal(OpState.Interrupted, _ledger.Get(opId)!.State);
        var task = _tasks.Get("t1")!;
        Assert.Equal((TaskState.NeedsAttention, RecoveryService.InterruptedLaunchReason), (task.State, task.AttentionReason));
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
        _lister.Processes.Add(new ProcessInfo(100, 1, _task.CliPath, string.Format(commandLine, session, session.ToUpperInvariant())));

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

    private static FilteredEvent Event(string type, string? subtype, Dictionary<string, string> fields, string? authSource = null) =>
        new(type, subtype, null, null, authSource, fields);

    [Fact]
    public void Exited_ClassifiedFromRecordedEvents()
    {
        var opId = ExitedOp(0);
        StreamEventLog.Append(_store, opId, Event("system", "init", [], "none"));
        StreamEventLog.Append(_store, opId, Event("result", "success", new() { ["is_error"] = "false", ["terminal_reason"] = "\"completed\"" }));

        Service().Reconcile();

        var op = _ledger.Get(opId)!;
        Assert.Equal((OpState.Classified, Outcome.Completed), (op.State, op.Outcome));
        Assert.Equal(TaskState.Completed, _tasks.Get("t1")!.State);
    }

    [Fact]
    public void Exited_ApiKeyAuthSource_BillingMismatch()
    {
        var opId = ExitedOp(0);
        StreamEventLog.Append(_store, opId, Event("system", "init", [], "ANTHROPIC_API_KEY"));
        StreamEventLog.Append(_store, opId, Event("result", "success", new() { ["is_error"] = "false", ["terminal_reason"] = "\"completed\"" }));

        Service().Reconcile();

        var op = _ledger.Get(opId)!;
        Assert.Equal((Outcome.BillingMismatch, "Billing unverified"), (op.Outcome, op.Reason));
        var task = _tasks.Get("t1")!;
        Assert.Equal((TaskState.NeedsAttention, "Billing unverified"), (task.State, task.AttentionReason));
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

    private void Sql(string sql)
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void MissingTaskRow_OpLeftLive_OtherOpsRecovered()
    {
        Sql("INSERT INTO launch_ops (op_id, task_id, window_id, attempt_no, state, created_utc) VALUES ('ghost-op', 'ghost', 0, 1, 'INTENT', 0)");
        var spawned = _ledger.BeginIntent("t1");
        _ledger.MarkSpawned(spawned, 100, 1, _task.SessionId);

        Service().Reconcile();

        Assert.Equal(OpState.Intent, _ledger.Get("ghost-op")!.State);
        Assert.Equal(OpState.Interrupted, _ledger.Get(spawned)!.State);
    }

    [Fact]
    public void Exited_CorruptEventRow_Ambiguous()
    {
        var opId = ExitedOp(0);
        Sql($"INSERT INTO events (op_id, kind, fields_json, at_utc) VALUES ('{opId}', 'stream', 'not json', 0)");

        Service().Reconcile();

        Assert.Equal(Outcome.Ambiguous, _ledger.Get(opId)!.Outcome);
        var task = _tasks.Get("t1")!;
        Assert.Equal((TaskState.NeedsAttention, "Stopped, reason unverified"), (task.State, task.AttentionReason));
    }

    [Fact]
    public void ListerThrowsUnexpectedly_TreatedAsListFailure()
    {
        var intent = _ledger.BeginIntent("t1");
        _lister.Unexpected = true;

        Service().Reconcile();

        Assert.Equal(OpState.Intent, _ledger.Get(intent)!.State);
        Assert.Equal("Process check failed", _tasks.Get("t1")!.AttentionReason);
    }

    [Fact]
    public void TaskUpdateFails_OpsStayLive_NextReconcileFinishes()
    {
        var intent = _ledger.BeginIntent("t1");
        _tasks.Add(TestTasks.Sample("t2"));
        var spawned = _ledger.BeginIntent("t2");
        _ledger.MarkSpawned(spawned, 100, 1, Guid.NewGuid());
        _tasks.Add(TestTasks.Sample("t3"));
        var exited = _ledger.BeginIntent("t3");
        _ledger.MarkSpawned(exited, 101, 1, Guid.NewGuid());
        _ledger.MarkExited(exited, 1);
        Sql("CREATE TRIGGER fail_task_update BEFORE UPDATE ON tasks BEGIN SELECT RAISE(ABORT, 'injected'); END");

        Service().Reconcile();
        var afterFault = new[] { intent, spawned, exited }.Select(id => _ledger.Get(id)!.State).ToArray();
        Sql("DROP TRIGGER fail_task_update");
        Service().Reconcile();

        Assert.Equal([OpState.Intent, OpState.Spawned, OpState.Exited], afterFault);
        Assert.Equal(
            [OpState.Abandoned, OpState.Interrupted, OpState.Classified],
            new[] { intent, spawned, exited }.Select(id => _ledger.Get(id)!.State));
        Assert.All(_tasks.List(), t => Assert.NotEqual(TaskState.Running, t.State));
    }

    public static TheoryData<string?, string?> UnknownProcesses() => new()
    {
        { TestTasks.Sample("x").CliPath, null },
        { TestTasks.Sample("x").CliPath.ToUpperInvariant(), "" },
        { null, null },
    };

    [Theory]
    [MemberData(nameof(UnknownProcesses))]
    public void Intent_UnreadableProcess_ProcessCheckFailed(string? executablePath, string? commandLine)
    {
        var intent = _ledger.BeginIntent("t1");
        _lister.Processes.Add(new ProcessInfo(100, 1, executablePath, commandLine));

        Service().Reconcile();

        Assert.Equal(OpState.Intent, _ledger.Get(intent)!.State);
        Assert.Equal((TaskState.NeedsAttention, "Process check failed"), (_tasks.Get("t1")!.State, _tasks.Get("t1")!.AttentionReason));
    }

    [Fact]
    public void Intent_EmptyProcessList_ProcessCheckFailed()
    {
        var intent = _ledger.BeginIntent("t1");
        _lister.Processes.Clear();

        Service().Reconcile();

        Assert.Equal(OpState.Intent, _ledger.Get(intent)!.State);
        Assert.Equal("Process check failed", _tasks.Get("t1")!.AttentionReason);
    }

    [Fact]
    public void Intent_OtherProcessWithUnreadableCommandLine_Abandoned()
    {
        var intent = _ledger.BeginIntent("t1");
        _lister.Processes.Add(new ProcessInfo(200, 1, @"C:\Windows\System32\notepad.exe", null));

        Service().Reconcile();

        Assert.Equal(OpState.Abandoned, _ledger.Get(intent)!.State);
    }

    [Fact]
    public void Intent_NoProcess_LastAttempt_NeedsAttention()
    {
        _tasks.Add(TestTasks.Sample("t2", maxAttempts: 1));
        var intent = _ledger.BeginIntent("t2");

        Service().Reconcile();

        Assert.Equal(OpState.Abandoned, _ledger.Get(intent)!.State);
        var task = _tasks.Get("t2")!;
        Assert.Equal((TaskState.NeedsAttention, "Attempts exhausted"), (task.State, task.AttentionReason));
    }

    [Fact]
    public void RunningTaskWithoutLiveOp_NeedsAttention()
    {
        _tasks.Add(TestTasks.Sample("t2") with { State = TaskState.Running, AttemptsUsed = 1 });
        _tasks.Add(TestTasks.Sample("t3") with { State = TaskState.WaitingForReset });

        Service().Reconcile();

        var stranded = _tasks.Get("t2")!;
        Assert.Equal((TaskState.NeedsAttention, "Interrupted run"), (stranded.State, stranded.AttentionReason));
        Assert.Equal(TaskState.WaitingForReset, _tasks.Get("t3")!.State);
        Assert.Equal(TaskState.Queued, _tasks.Get("t1")!.State);
    }

    [Fact]
    public void Intent_NoProcess_ClearsEarlierSchedule()
    {
        _tasks.SetSchedule("t1", 1_800_000_000, 1_800_000_120);
        _ledger.BeginIntent("t1");

        Service().Reconcile();

        var task = _tasks.Get("t1")!;
        Assert.Equal(TaskState.WaitingForReset, task.State);
        Assert.Null(task.ResetExpectedAtEpoch);
        Assert.Null(task.AttemptPermittedAtEpoch);
    }

    private sealed class FakeLister : IProcessLister
    {
        public List<ProcessInfo> Processes { get; } = [];

        public bool Fail { get; set; }

        public bool Unexpected { get; set; }

        public int Calls { get; private set; }

        public IReadOnlyList<ProcessInfo> ListOwnProcesses()
        {
            Calls++;
            if (Unexpected)
            {
                throw new UnauthorizedAccessException("access denied");
            }
            return Fail ? throw new ProcessListException("enumeration failed") : Processes;
        }
    }
}
