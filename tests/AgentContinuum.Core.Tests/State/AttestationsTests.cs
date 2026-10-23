using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.State;

public sealed class AttestationsTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;

    public AttestationsTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Latest_ReturnsLastRecordedOfKind()
    {
        var attestations = new Attestations(_store);
        var recordedWhileClockAhead = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var recordedAfterFix = recordedWhileClockAhead.AddDays(-10);

        attestations.Record(Attestations.OverageOff, recordedWhileClockAhead);
        attestations.Record(Attestations.OverageOff, recordedAfterFix);
        attestations.Record("other", recordedWhileClockAhead.AddDays(5));

        Assert.Equal(recordedAfterFix, attestations.Latest(Attestations.OverageOff));
        Assert.Null(attestations.Latest("missing"));
    }
}
