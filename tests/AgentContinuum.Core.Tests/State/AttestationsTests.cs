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
    public void Latest_ReturnsNewestOfKind()
    {
        var attestations = new Attestations(_store);
        var older = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = older.AddDays(10);

        attestations.Record(Attestations.OverageOff, newer);
        attestations.Record(Attestations.OverageOff, older);
        attestations.Record("other", newer.AddDays(5));

        Assert.Equal(newer, attestations.Latest(Attestations.OverageOff));
        Assert.Null(attestations.Latest("missing"));
    }
}
