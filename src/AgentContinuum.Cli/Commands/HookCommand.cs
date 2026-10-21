using AgentContinuum.Core.Events;
using AgentContinuum.Core.State;

namespace AgentContinuum.Cli.Commands;

/// <summary>Run by Claude Code as its StopFailure hook.</summary>
public static class HookCommand
{
    // Anything the hook prints reaches the agent and a failing hook can change how the run ends, so it never
    // writes to stdout and always exits 0. Only a live op records, so a stray or replayed call adds nothing.
    public static int StopFailure(AppServices services, IReadOnlyList<string> args)
    {
        try
        {
            if (args is not ["--op", var opId])
            {
                services.Log.Write("hook called with bad arguments");
                return 0;
            }
            using var store = services.OpenStore();
            var ledger = new LaunchLedger(store);
            if (ledger.Get(opId) is not { } op || ledger.Live(op.TaskId)?.OpId != opId)
            {
                services.Log.Write($"hook for op {opId} ignored: not live");
                return 0;
            }
            var task = new TaskRepository(store).Get(op.TaskId);
            HookRecorder.Record(store, opId, services.In, task is null ? null : services.RulesFor(task.CliVersion));
        }
        catch (Exception e)
        {
            services.Log.Write($"hook failed: {e.GetType().Name}");
        }
        return 0;
    }
}
