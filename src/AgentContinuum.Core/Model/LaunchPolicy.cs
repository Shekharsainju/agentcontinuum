namespace AgentContinuum.Core.Model;

/// <summary>What a task must be for a launch to go ahead.</summary>
public static class LaunchPolicy
{
    public static readonly TaskState[] LaunchableStates =
        [TaskState.Queued, TaskState.WaitingForReset, TaskState.ResumeOffered, TaskState.NeedsAttention];

    public static readonly string[] PermissionModes = ["default", "acceptEdits", "plan"];

    public static bool IsLaunchable(TaskState state) => LaunchableStates.Contains(state);

    public static bool IsAllowedMode(string mode) => PermissionModes.Contains(mode, StringComparer.Ordinal);
}
