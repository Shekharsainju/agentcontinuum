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

        // A crash between a task update and its op transition outside recovery can leave RUNNING with no live op.
        foreach (var task in _tasks.List().Where(t => t.State == TaskState.Running && _ledger.Live(t.TaskId) is null))
        {
            TryFlag(task.TaskId, InterruptedRunReason);
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
            // A recorded process ran, or was about to, and job close ended it with the daemon, so no listing can
            // tell more and the work it may have done needs you.
            case OpState.Intent when op.Pid is not null:
                RecoverIntent(op, task, processExisted: true);
                break;
            case OpState.Intent:
                if ((processes.Value is { } list ? SessionHasProcess(list, task) : null) is not { } hasProcess)
                {
                    // The op stays INTENT: without a reliable process list it is unknown whether work happened.
                    _tasks.SetState(op.TaskId, TaskState.NeedsAttention, ProcessCheckFailedReason);
                    return;
                }
                RecoverIntent(op, task, hasProcess);
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

    private void RecoverIntent(LaunchOp op, TaskRecord task, bool processExisted)
    {
        if (processExisted)
        {
            _tasks.SetState(op.TaskId, TaskState.NeedsAttention, InterruptedLaunchReason);
            _ledger.Interrupt(op.OpId);
            return;
        }

        if (task.AttemptsUsed >= task.MaxAttempts)
        {
            _tasks.SetState(op.TaskId, TaskState.NeedsAttention, OutcomeApplier.AttemptsExhaustedReason);
        }
        else
        {
            // No schedule tells the scheduler to offer at once and say the launch did not start.
            _tasks.SetSchedule(op.TaskId, null, null);
            _tasks.SetState(op.TaskId, TaskState.WaitingForReset, null);
        }
        _ledger.Abandon(op.OpId);
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

    // Null means unknown. The list always holds the controller itself, so an empty one is a failed listing,
    // and a CLI process whose command line cannot be read may belong to this session.
    private static bool? SessionHasProcess(IReadOnlyList<ProcessInfo> processes, TaskRecord task)
    {
        if (processes.Count == 0)
        {
            return null;
        }

        var session = task.SessionId.ToString("D");
        if (processes.Any(p => p.CommandLine?.Contains(session, StringComparison.OrdinalIgnoreCase) == true))
        {
            return true;
        }

        if (processes.Any(p => string.IsNullOrEmpty(p.CommandLine)
            && (p.ExecutablePath is null || string.Equals(p.ExecutablePath, task.CliPath, StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }

        return false;
    }
}
