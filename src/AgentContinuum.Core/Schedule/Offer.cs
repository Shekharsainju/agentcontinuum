namespace AgentContinuum.Core.Schedule;

/// <summary><see cref="LaunchDidNotStart"/> marks a launch abandoned before its process ran, so no reset was awaited.</summary>
public sealed record Offer(string TaskId, string Label, bool ResetTimeUnknown, bool LaunchDidNotStart);
