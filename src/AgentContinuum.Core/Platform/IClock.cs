namespace AgentContinuum.Core.Platform;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Milliseconds from a source that keeps counting through sleep and ignores wall-clock changes.</summary>
    long MonotonicMs { get; }
}
