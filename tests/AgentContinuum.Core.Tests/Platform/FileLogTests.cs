using AgentContinuum.Core.Platform;

namespace AgentContinuum.Core.Tests.Platform;

public sealed class FileLogTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Write_AppendsTimestampedLinesFromManyThreads()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.Zero));
        var path = Path.Combine(_dir.Path, "logs", "ac.log");
        var log = new FileLog(path, clock);

        Parallel.For(0, 50, i => log.Write($"message {i}"));

        var lines = File.ReadAllLines(path);
        Assert.Equal(50, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("2026-10-22T09:30:00Z message ", line));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"message {i}").Order(), lines.Select(l => l[21..]).Order());
    }
}
