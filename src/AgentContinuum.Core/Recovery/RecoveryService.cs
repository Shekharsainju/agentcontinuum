using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Recovery;

public sealed class RecoveryService
{
    public const string InterruptedLaunchReason = "Interrupted launch";

    private readonly StateStore _store;
    private readonly LaunchLedger _ledger;
    private readonly TaskRepository _tasks;
    private readonly IProcessLister _lister;
    private readonly OutcomeApplier _outcomes;

    public RecoveryService(StateStore store, LaunchLedger ledger, TaskRepository tasks, IProcessLister lister, OutcomeApplier outcomes)
    {
        _store = store;
        _ledger = ledger;
        _tasks = tasks;
        _lister = lister;
        _outcomes = outcomes;
    }

    public void Reconcile()
    {
        IReadOnlyList<ProcessInfo>? processes = null;
        foreach (var op in _ledger.AllLive())
        {
            if (op.State == OpState.Intent)
            {
                var task = _tasks.Get(op.TaskId) ?? throw new InvalidOperationException($"No task {op.TaskId}.");
                processes ??= _lister.ListOwnProcesses();
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
        }
    }

    private static bool HasSessionProcess(IReadOnlyList<ProcessInfo> processes, Guid sessionId)
    {
        var session = sessionId.ToString("D");
        return processes.Any(p => p.CommandLine.Contains(session, StringComparison.OrdinalIgnoreCase));
    }
}
