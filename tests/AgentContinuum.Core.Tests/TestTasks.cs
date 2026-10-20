using AgentContinuum.Core.Model;

namespace AgentContinuum.Core.Tests;

public static class TestTasks
{
    public static TaskRecord Sample(string taskId, int maxAttempts = 3) => new(
        TaskId: taskId,
        Label: "sample",
        Worktree: @"C:\work\repo",
        PromptFile: @"C:\work\repo\prompt.md",
        PermissionMode: "acceptEdits",
        AllowedTools: ["Read", "Edit"],
        MaxAttempts: maxAttempts,
        DeadlineUtc: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        SessionId: Guid.Parse("6f9c1f3e-2b7a-4c55-9d0e-1a2b3c4d5e6f"),
        CliPath: @"C:\Users\me\.local\bin\claude.exe",
        CliSha256: new string('a', 64),
        CliVersion: "2.1.304",
        State: TaskState.Queued,
        AttemptsUsed: 0,
        ResetExpectedAtEpoch: null,
        AttemptPermittedAtEpoch: null,
        BaselineFingerprint: null,
        AttentionReason: null);
}
