namespace AgentContinuum.Core.Model;

public sealed record TaskRecord(
    string TaskId,
    string Label,
    string Worktree,
    string PromptFile,
    string PermissionMode,
    string[] AllowedTools,
    int MaxAttempts,
    DateTimeOffset DeadlineUtc,
    Guid SessionId,
    string CliPath,
    string CliSha256,
    string CliVersion,
    TaskState State,
    int AttemptsUsed,
    long? ResetExpectedAtEpoch,
    long? AttemptPermittedAtEpoch,
    string? BaselineFingerprint,
    string? AttentionReason);
