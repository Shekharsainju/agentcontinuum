using AgentContinuum.Core.Model;
using AgentContinuum.Core.Schedule;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.Schedule;

public sealed class ResetSchedulerTests : IDisposable
{
    private const long Reset = 1_800_000_000;

    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;
    private readonly TaskRepository _tasks;
    private readonly FakeClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(Reset - 3600));
    private readonly RecordingLog _log = new();
    private readonly ResetScheduler _scheduler;

    public ResetSchedulerTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
        _tasks = new TaskRepository(_store);
        _tasks.Add(TestTasks.Sample("t1") with { State = TaskState.Running, AttemptsUsed = 1 });
        _scheduler = new ResetScheduler(_tasks, _clock, _log);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Permitted_IsResetPlus2Min()
    {
        _scheduler.OnUsageLimit("t1", Reset);

        var task = _tasks.Get("t1")!;
        Assert.Equal(TaskState.WaitingForReset, task.State);
        Assert.Equal(Reset, task.ResetExpectedAtEpoch);
        Assert.Equal(Reset + 120, task.AttemptPermittedAtEpoch);
    }

    [Fact]
    public void UnknownReset_PermittedNow()
    {
        _scheduler.OnUsageLimit("t1", null);

        var task = _tasks.Get("t1")!;
        Assert.Equal(TaskState.WaitingForReset, task.State);
        Assert.Null(task.ResetExpectedAtEpoch);
        Assert.Equal(_clock.UtcNow.ToUnixTimeSeconds(), task.AttemptPermittedAtEpoch);
    }

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    private List<Offer> TickEvery30sFor(TimeSpan span)
    {
        var offers = new List<Offer>();
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += Poll)
        {
            _clock.Advance(Poll);
            offers.AddRange(_scheduler.Tick());
        }
        return offers;
    }

    [Fact]
    public void Tick_BeforePermitted_NoOffer()
    {
        _scheduler.OnUsageLimit("t1", Reset);
        _clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(Reset + 119);

        Assert.Empty(_scheduler.Tick());
        Assert.Equal(TaskState.WaitingForReset, _tasks.Get("t1")!.State);
    }

    [Fact]
    public void Tick_AfterPermitted_ExactlyOneOffer()
    {
        _scheduler.OnUsageLimit("t1", Reset);
        _clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(Reset + 120);

        var offer = Assert.Single(_scheduler.Tick());
        var later = TickEvery30sFor(TimeSpan.FromMinutes(1));

        Assert.Equal(("t1", "sample", false), (offer.TaskId, offer.Label, offer.ResetTimeUnknown));
        Assert.Empty(later);
        Assert.Equal(TaskState.ResumeOffered, _tasks.Get("t1")!.State);
    }

    [Fact]
    public void UnknownReset_OffersAtOnceMarkedUnknown()
    {
        _scheduler.OnUsageLimit("t1", null);

        var offer = Assert.Single(_scheduler.Tick());

        Assert.True(offer.ResetTimeUnknown);
    }

    [Fact]
    public void Tick_IgnoresTasksNotWaiting()
    {
        _tasks.Add(TestTasks.Sample("t2") with { State = TaskState.NeedsAttention, AttemptPermittedAtEpoch = Reset });
        _clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(Reset + 3600);

        Assert.Empty(_scheduler.Tick());
    }

    [Fact]
    public void ClockJumpForward2h_AtMostOneOffer()
    {
        _scheduler.OnUsageLimit("t1", Reset);
        Assert.Empty(_scheduler.Tick());

        _clock.JumpWallClock(TimeSpan.FromHours(2));
        var offers = _scheduler.Tick().Concat(TickEvery30sFor(TimeSpan.FromHours(3))).ToList();

        Assert.Single(offers);
    }

    [Fact]
    public void ClockJumpBack2h_NoOfferUntilPermitted()
    {
        _scheduler.OnUsageLimit("t1", Reset);
        _clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(Reset + 60);
        Assert.Empty(_scheduler.Tick());

        _clock.JumpWallClock(TimeSpan.FromHours(-2));
        var offerTimes = new List<long>();
        for (var i = 0; i < 300; i++)
        {
            _clock.Advance(Poll);
            if (_scheduler.Tick().Count > 0)
            {
                offerTimes.Add(_clock.UtcNow.ToUnixTimeSeconds());
            }
        }

        var offeredAt = Assert.Single(offerTimes);
        Assert.InRange(offeredAt, Reset + 120, Reset + 120 + 30);
    }

    [Fact]
    public void SleepAcrossReset_OneOfferOnWake()
    {
        _scheduler.OnUsageLimit("t1", Reset);
        Assert.Empty(_scheduler.Tick());

        _clock.Advance(TimeSpan.FromHours(6));
        var onWake = _scheduler.Tick();
        var after = TickEvery30sFor(TimeSpan.FromMinutes(5));

        Assert.Single(onWake);
        Assert.Empty(after);
    }

    [Fact]
    public void WaitingWithoutSchedule_OffersAtOnceAsLaunchNotStarted()
    {
        _tasks.Add(TestTasks.Sample("t2") with { State = TaskState.WaitingForReset, AttemptsUsed = 1 });

        var offer = Assert.Single(_scheduler.Tick());

        Assert.Equal(("t2", true, false), (offer.TaskId, offer.LaunchDidNotStart, offer.ResetTimeUnknown));
        Assert.Equal(TaskState.ResumeOffered, _tasks.Get("t2")!.State);
        Assert.Empty(_scheduler.Tick());
    }
}
