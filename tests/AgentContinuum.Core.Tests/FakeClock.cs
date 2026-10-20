using AgentContinuum.Core.Platform;

namespace AgentContinuum.Core.Tests;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset start)
    {
        UtcNow = start;
    }

    public DateTimeOffset UtcNow { get; set; }

    public long MonotonicMs { get; set; }

    public void Advance(TimeSpan elapsed)
    {
        UtcNow += elapsed;
        MonotonicMs += (long)elapsed.TotalMilliseconds;
    }

    public void JumpWallClock(TimeSpan jump) => UtcNow += jump;
}
