namespace AgentContinuum.Core.Events;

/// <summary>A recorded hook call. <see cref="Fields"/> maps dotted paths to JSON literals.</summary>
public sealed record HookEvent(string HookName, string? SessionId, IReadOnlyDictionary<string, string> Fields);
