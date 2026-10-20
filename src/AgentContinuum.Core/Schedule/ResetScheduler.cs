using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Schedule;

public sealed class ResetScheduler : IUsageLimitHandler
{
    public const long ResetMarginSeconds = 120;
    public const long ClockJumpThresholdSeconds = 120;

    private readonly TaskRepository _tasks;
    private readonly IClock _clock;
    private readonly ILog _log;
    private (long WallMs, long MonotonicMs)? _lastTick;

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

    // The move to RESUME_OFFERED is guarded on WAITING_FOR_RESET, so a task is offered at most once per wait.
    public IReadOnlyList<Offer> Tick()
    {
        DetectClockJump();
        var now = _clock.UtcNow.ToUnixTimeSeconds();
        var offers = new List<Offer>();
        foreach (var task in _tasks.List())
        {
            if (task.State == TaskState.WaitingForReset
                && (task.AttemptPermittedAtEpoch is not { } permitted || now >= permitted)
                && _tasks.TryMoveState(task.TaskId, TaskState.WaitingForReset, TaskState.ResumeOffered))
            {
                var notStarted = task.AttemptPermittedAtEpoch is null;
                offers.Add(new Offer(task.TaskId, task.Label, !notStarted && task.ResetExpectedAtEpoch is null, notStarted));
            }
        }
        return offers;
    }

    // Schedules are absolute UTC epochs, so recomputing after a jump is the comparison this Tick makes against
    // the new wall time; the guarded move keeps that to one offer.
    private void DetectClockJump()
    {
        var now = (WallMs: _clock.UtcNow.ToUnixTimeMilliseconds(), MonotonicMs: _clock.MonotonicMs);
        if (_lastTick is { } last)
        {
            var driftSeconds = ((now.WallMs - last.WallMs) - (now.MonotonicMs - last.MonotonicMs)) / 1000;
            if (Math.Abs(driftSeconds) > ClockJumpThresholdSeconds)
            {
                _log.Write($"clock jump {driftSeconds}s against the monotonic clock; schedules recomputed");
            }
        }
        _lastTick = now;
    }
}
