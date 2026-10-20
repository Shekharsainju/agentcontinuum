namespace AgentContinuum.Core.Model;

public enum TaskState
{
    Queued,
    Running,
    WaitingForReset,
    ResumeOffered,
    NeedsAttention,
    Completed,
    Revoked,
}
