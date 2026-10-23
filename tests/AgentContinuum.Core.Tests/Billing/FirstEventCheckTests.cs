using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Tests.Billing;

public sealed class FirstEventCheckTests
{
    private static readonly ClaudeRules Rules = VersionRules.LoadEmbedded().Entries[0];

    private static FilteredEvent Event(string type, string? subtype = null, string? authSource = null) =>
        new(type, subtype, "s1", null, authSource, new Dictionary<string, string>());

    private static readonly FilteredEvent HookStarted = Event("system", "hook_started");
    private static readonly FilteredEvent Init = Event("system", "init", "none");
    private static readonly FilteredEvent ApiKeyInit = Event("system", "init", "ANTHROPIC_API_KEY");
    private static readonly FilteredEvent Assistant = Event("assistant");
    private static readonly FilteredEvent Result = Event("result", "success");

    [Fact]
    public void Matches_OnlyTheP5EventWithASubscriptionValue()
    {
        Assert.True(FirstEventCheck.Matches(Init, Rules));
        Assert.False(FirstEventCheck.Matches(ApiKeyInit, Rules));
        Assert.False(FirstEventCheck.Matches(Event("system", "init"), Rules));
        Assert.False(FirstEventCheck.Matches(Event("system", "hook_started", "none"), Rules));
        Assert.False(FirstEventCheck.Matches(Init, Rules with { P5 = null }));
    }

    [Fact]
    public void Over_InitAfterHookEvents_Verified()
    {
        Assert.Equal(FirstEventStatus.Verified, FirstEventCheck.Over([HookStarted, HookStarted, Init, Assistant, Result], Rules));
    }

    [Fact]
    public void Over_ApiKeyInit_Mismatch()
    {
        Assert.Equal(FirstEventStatus.Mismatch, FirstEventCheck.Over([HookStarted, ApiKeyInit, Assistant], Rules));
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("result")]
    public void Over_ContentBeforeInit_Mismatch(string type)
    {
        Assert.Equal(FirstEventStatus.Mismatch, FirstEventCheck.Over([HookStarted, Event(type), Init], Rules));
    }

    [Fact]
    public void Over_NoInitYet_Pending()
    {
        Assert.Equal(FirstEventStatus.Pending, FirstEventCheck.Over([HookStarted], Rules));
        Assert.Equal(FirstEventStatus.Pending, FirstEventCheck.Over([], Rules));
    }

    [Fact]
    public void Over_LaterApiKeyInitAfterVerified_Mismatch()
    {
        Assert.Equal(FirstEventStatus.Mismatch, FirstEventCheck.Over([Init, ApiKeyInit], Rules));
        Assert.Equal(FirstEventStatus.Verified, FirstEventCheck.Over([Init, Init], Rules));
    }

    [Fact]
    public void Observe_NoRule_Mismatch()
    {
        Assert.Equal(FirstEventStatus.Mismatch, FirstEventCheck.Observe(FirstEventStatus.Pending, HookStarted, null));
        Assert.Equal(FirstEventStatus.Mismatch, FirstEventCheck.Observe(FirstEventStatus.Pending, Init, Rules with { P5 = null }));
    }
}
