using System.Diagnostics;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.State;

public sealed class RequestQueueTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
    private readonly RequestQueue _queue;

    public RequestQueueTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
        _queue = new RequestQueue(_store, _clock);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Submit_NextComplete_WaitForReturnsResultOnce()
    {
        var id = _queue.Submit("resume", "t1", """{"now":true}""");

        var request = _queue.Next()!;
        Assert.Equal(new Request(id, "resume", "t1", """{"now":true}""", _clock.UtcNow), request);
        Assert.Null(_queue.Next());

        _queue.Complete(id, """{"ok":true}""");
        Assert.Equal("""{"ok":true}""", _queue.WaitFor(id, TimeSpan.FromSeconds(1)));
        Assert.Null(_queue.WaitFor(id, TimeSpan.Zero));
    }

    [Fact]
    public void Next_TakesOldestFirst()
    {
        var first = _queue.Submit("start", "t1", "{}");
        _queue.Submit("start", "t2", "{}");

        Assert.Equal(first, _queue.Next()!.Id);
    }

    [Fact]
    public void WaitFor_NoReply_NullAfterTimeout()
    {
        var id = _queue.Submit("start", "t1", "{}");
        var watch = Stopwatch.StartNew();

        Assert.Null(_queue.WaitFor(id, TimeSpan.FromMilliseconds(300)));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public void Withdraw_OnlyWhileNotTaken()
    {
        var waiting = _queue.Submit("start", "t1", "{}");
        Assert.True(_queue.Withdraw(waiting));
        Assert.Null(_queue.Next());

        var taken = _queue.Submit("start", "t1", "{}");
        _queue.Next();
        Assert.False(_queue.Withdraw(taken));
    }

    [Fact]
    public void ExpireOlderThan_DropsOldUnfinishedRequests()
    {
        _queue.Submit("resume", "old", "{}");
        var oldTaken = _queue.Submit("resume", "old-taken", "{}");
        _queue.Next();
        _queue.Next();
        _clock.Advance(TimeSpan.FromSeconds(61));
        var fresh = _queue.Submit("resume", "fresh", "{}");

        Assert.Equal(2, _queue.ExpireOlderThan(TimeSpan.FromSeconds(60)));

        Assert.Equal(fresh, _queue.Next()!.Id);
        _queue.Complete(oldTaken, "{}");
        Assert.Null(_queue.WaitFor(oldTaken, TimeSpan.Zero));
    }
}
