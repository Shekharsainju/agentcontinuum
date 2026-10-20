using System.Text.Json;
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
    public const string ProcessCheckFailedReason = "Process check failed";
    public const string RecoveryFailedReason = "Recovery failed";

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
    // live, so the next Reconcile repeats the same step instead of stranding the task. A failure on one op
    // leaves that op live and flags its task, so the remaining ops are still recovered.
    public void Reconcile()
    {
        var processes = new Lazy<IReadOnlyList<ProcessInfo>?>(ListProcesses);
        foreach (var op in _ledger.AllLive())
        {
            try
            {
                Recover(op, processes);
            }
            catch (Exception)
            {
                TryFlag(op.TaskId, RecoveryFailedReason);
            }
        }
    }

    private void Recover(LaunchOp op, Lazy<IReadOnlyList<ProcessInfo>?> processes)
    {
        if (_tasks.Get(op.TaskId) is not { } task)
        {
            return;
        }

        switch (op.State)
        {
            case OpState.Intent:
                if (processes.Value is not { } list)
                {
                    // The op stays INTENT: without a process list it is unknown whether work happened.
                    _tasks.SetState(op.TaskId, TaskState.NeedsAttention, ProcessCheckFailedReason);
                    return;
                }
                RecoverIntent(op, task, list);
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
        Classification classification;
        try
        {
            classification = RunClassifier.Classify(
                _rulesFor(task.CliVersion),
                task.SessionId,
                StreamEventLog.ForOp(_store, op.OpId),
                HookRecorder.ForOp(_store, op.OpId),
                op.ExitCode ?? -1);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            classification = new Classification(Outcome.Ambiguous, RunClassifier.AmbiguousReason, null);
        }
        _outcomes.Apply(op.TaskId, classification);
        _ledger.Classify(op.OpId, classification.Outcome, classification.Reason);
    }

    // Any lister failure counts as an unknown process state, not only the documented exception.
    private IReadOnlyList<ProcessInfo>? ListProcesses()
    {
        try
        {
            return _lister.ListOwnProcesses();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void TryFlag(string taskId, string reason)
    {
        try
        {
            _tasks.SetState(taskId, TaskState.NeedsAttention, reason);
        }
        catch (Exception)
        {
        }
    }

    private static bool HasSessionProcess(IReadOnlyList<ProcessInfo> processes, Guid sessionId)
    {
        var session = sessionId.ToString("D");
        return processes.Any(p => p.CommandLine.Contains(session, StringComparison.OrdinalIgnoreCase));
    }
}
