namespace AgentContinuum.Core.Platform;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    // GetTickCount64 keeps counting through sleep and hibernation, which the reset schedule relies on.
    public long MonotonicMs => Environment.TickCount64;
}
