using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Schedule;

public sealed class ResetScheduler : IUsageLimitHandler
{
    public const long ResetMarginSeconds = 120;

    private readonly TaskRepository _tasks;
    private readonly IClock _clock;
    private readonly ILog _log;

    public ResetScheduler(TaskRepository tasks, IClock clock, ILog log)
    {
        _tasks = tasks;
        _clock = clock;
        _log = log;
    }

    public void OnUsageLimit(string taskId, long? resetEpoch)
    {
        var permitted = resetEpoch is { } reset ? reset + ResetMarginSeconds : _clock.UtcNow.ToUnixTimeSeconds();
        _tasks.SetSchedule(taskId, resetEpoch, permitted);
        _tasks.SetState(taskId, TaskState.WaitingForReset, null);
    }
}
