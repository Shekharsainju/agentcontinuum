namespace AgentContinuum.Core.Model;

/// <summary>What a task must be for a launch to go ahead.</summary>
public static class LaunchPolicy
{
    public static readonly TaskState[] LaunchableStates =
        [TaskState.Queued, TaskState.WaitingForReset, TaskState.ResumeOffered, TaskState.NeedsAttention];

    public static readonly string[] PermissionModes = ["default", "acceptEdits", "plan"];

    public static bool IsLaunchable(TaskState state) => LaunchableStates.Contains(state);

    public static bool IsAllowedMode(string mode) => PermissionModes.Contains(mode, StringComparer.Ordinal);

    // Each rule follows --allowedTools as its own argument, so one that starts with - would be read as a new flag,
    // such as a second --permission-mode after the checked one.
    public static bool IsAllowedToolRule(string rule) =>
        !string.IsNullOrWhiteSpace(rule) && !rule.TrimStart().StartsWith('-') && !rule.Any(char.IsControl);
}
