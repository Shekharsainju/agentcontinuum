using AgentContinuum.Core.Schedule;

namespace AgentContinuum.Core.Tests;

public sealed class RecordingUsageLimits : IUsageLimitHandler
{
    public List<(string TaskId, long? ResetEpoch)> Calls { get; } = [];

    public void OnUsageLimit(string taskId, long? resetEpoch) => Calls.Add((taskId, resetEpoch));
}
