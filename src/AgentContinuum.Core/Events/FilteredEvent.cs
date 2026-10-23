namespace AgentContinuum.Core.Events;

/// <summary>An allowlisted view of one stream-json line. <see cref="LimitFields"/> maps dotted paths to JSON literals.</summary>
public sealed record FilteredEvent(
    string Type,
    string? Subtype,
    string? SessionId,
    bool? IsError,
    string? AuthSource,
    IReadOnlyDictionary<string, string> LimitFields);
