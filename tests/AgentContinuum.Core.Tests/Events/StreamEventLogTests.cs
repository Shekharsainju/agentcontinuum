using AgentContinuum.Core.Events;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.Events;

public sealed class StreamEventLogTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;

    public StreamEventLogTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Append_ForOpReturnsEventsInOrder()
    {
        var init = new FilteredEvent("system", "init", "s1", null, "none", new Dictionary<string, string>());
        var result = new FilteredEvent("result", "success", "s1", false, null, new Dictionary<string, string>
        {
            ["is_error"] = "false",
            ["terminal_reason"] = "\"completed\"",
        });

        StreamEventLog.Append(_store, "op1", init);
        StreamEventLog.Append(_store, "op2", init);
        StreamEventLog.Append(_store, "op1", result);
        HookRecorder.Record(_store, "op1", new MemoryStream("{\"hook_event_name\":\"Stop\"}"u8.ToArray()), null);

        var events = StreamEventLog.ForOp(_store, "op1");

        Assert.Equal(2, events.Count);
        Assert.Equivalent(init, events[0], strict: true);
        Assert.Equivalent(result, events[1], strict: true);
    }
}
