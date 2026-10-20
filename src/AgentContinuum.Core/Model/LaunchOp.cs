namespace AgentContinuum.Core.Model;

public sealed record LaunchOp(
    string OpId,
    string TaskId,
    int WindowId,
    int AttemptNo,
    OpState State,
    int? Pid,
    long? ProcCreatedAtFileTime,
    Guid? SessionId,
    int? ExitCode,
    Outcome? Outcome,
    string? Reason,
    long CreatedAtEpoch);
