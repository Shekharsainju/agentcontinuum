using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Recovery;
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
