using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Recovery;

public sealed class RecoveryService
{
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
        foreach (var op in _ledger.AllLive())
        {
            if (op.State == OpState.Intent)
            {
                _tasks.SetState(op.TaskId, TaskState.WaitingForReset, null);
                _ledger.Abandon(op.OpId);
            }
        }
    }
}
