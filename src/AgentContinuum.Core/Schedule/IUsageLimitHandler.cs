namespace AgentContinuum.Core.Schedule;

public interface IUsageLimitHandler
{
    void OnUsageLimit(string taskId, long? resetEpoch);
}
