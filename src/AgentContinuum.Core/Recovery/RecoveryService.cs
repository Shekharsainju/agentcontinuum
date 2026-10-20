using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Recovery;

public sealed class RecoveryService
{
    public const string InterruptedLaunchReason = "Interrupted launch";
    public const string InterruptedRunReason = "Interrupted run";

    private readonly StateStore _store;
    private readonly LaunchLedger _ledger;
    private readonly TaskRepository _tasks;
    private readonly IProcessLister _lister;
    private readonly OutcomeApplier _outcomes;
    private readonly Func<string, ClaudeRules?> _rulesFor;

    public RecoveryService(
        StateStore store,
        LaunchLedger ledger,
        TaskRepository tasks,
        IProcessLister lister,
        OutcomeApplier outcomes,
        Func<string, ClaudeRules?>? rulesFor = null)
    {
        _store = store;
        _ledger = ledger;
        _tasks = tasks;
        _lister = lister;
        _outcomes = outcomes;
        _rulesFor = rulesFor ?? VersionRules.For;
    }

    // Each row updates the task before moving the op out of its live state. A crash in between leaves the op
    // live, so the next Reconcile repeats the same step instead of stranding the task.
    public void Reconcile()
    {
        IReadOnlyList<ProcessInfo>? processes = null;
        foreach (var op in _ledger.AllLive())
        {
            var task = _tasks.Get(op.TaskId) ?? throw new InvalidOperationException($"No task {op.TaskId}.");
            switch (op.State)
            {
                case OpState.Intent:
                    processes ??= _lister.ListOwnProcesses();
                    RecoverIntent(op, task, processes);
                    break;
                case OpState.Spawned:
                    _tasks.SetState(op.TaskId, TaskState.NeedsAttention, InterruptedRunReason);
                    _ledger.Interrupt(op.OpId);
                    break;
                case OpState.Exited:
                    RecoverExited(op, task);
                    break;
            }
        }
    }

    private void RecoverIntent(LaunchOp op, TaskRecord task, IReadOnlyList<ProcessInfo> processes)
    {
        if (HasSessionProcess(processes, task.SessionId))
        {
            _tasks.SetState(op.TaskId, TaskState.NeedsAttention, InterruptedLaunchReason);
            _ledger.Interrupt(op.OpId);
        }
        else
        {
            _tasks.SetState(op.TaskId, TaskState.WaitingForReset, null);
            _ledger.Abandon(op.OpId);
        }
    }

    private void RecoverExited(LaunchOp op, TaskRecord task)
    {
        var classification = RunClassifier.Classify(
            _rulesFor(task.CliVersion),
            task.SessionId,
            StreamEventLog.ForOp(_store, op.OpId),
            HookRecorder.ForOp(_store, op.OpId),
            op.ExitCode ?? -1);
        _outcomes.Apply(op.TaskId, classification);
        _ledger.Classify(op.OpId, classification.Outcome, classification.Reason);
    }

    private static bool HasSessionProcess(IReadOnlyList<ProcessInfo> processes, Guid sessionId)
    {
        var session = sessionId.ToString("D");
        return processes.Any(p => p.CommandLine.Contains(session, StringComparison.OrdinalIgnoreCase));
    }
}
