using AgentContinuum.Core.Model;
using AgentContinuum.Core.Schedule;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Classify;

public sealed class OutcomeApplier
{
    public const string AttemptsExhaustedReason = "Attempts exhausted";

    private readonly TaskRepository _tasks;
    private readonly IUsageLimitHandler _usageLimits;

    public OutcomeApplier(TaskRepository tasks, IUsageLimitHandler usageLimits)
    {
        _tasks = tasks;
        _usageLimits = usageLimits;
    }

    public void Apply(string taskId, Classification classification)
    {
        switch (classification.Outcome)
        {
            case Outcome.Completed:
                _tasks.SetState(taskId, TaskState.Completed, null);
                break;
            case Outcome.UsageLimit:
                var task = _tasks.Get(taskId) ?? throw new InvalidOperationException($"No task {taskId}.");
                if (task.AttemptsUsed >= task.MaxAttempts)
                {
                    _tasks.SetState(taskId, TaskState.NeedsAttention, AttemptsExhaustedReason);
                }
                else
                {
                    _usageLimits.OnUsageLimit(taskId, classification.ResetEpoch);
                }
                break;
            default:
                _tasks.SetState(taskId, TaskState.NeedsAttention, classification.Reason);
                break;
        }
    }
}
