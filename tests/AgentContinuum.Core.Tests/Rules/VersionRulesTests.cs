using System.Text.Json;
using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Tests.Rules;

public sealed class VersionRulesTests
{
    [Fact]
    public void LoadEmbedded_Parses2_1_304Entry()
    {
        var entry = VersionRules.LoadEmbedded().Entries[0];

        Assert.Equal(">=2.1.304 <2.1.305", entry.Versions);
        Assert.Empty(entry.P2ExtraKeys);
        Assert.Equal(".claude/.credentials.json", entry.P3!.File);
        Assert.Equal(["primaryApiKey"], entry.P3.ApiKeyKeys);
        Assert.Equal(["auth", "status"], entry.P4!.Args);
        Assert.Equal("authMethod", entry.P4.Require[1].Path);
        Assert.Equal("claude.ai", entry.P4.Require[1].Values[0].GetString());
        Assert.Equal(JsonValueKind.True, entry.P4.Require[0].Values[0].ValueKind);
        Assert.Equal(["subscriptionType"], entry.P4.RequirePresent);
        Assert.Equal(("system", "init", "apiKeySource"), (entry.P5!.Type, entry.P5.Subtype, entry.P5.Field));
        Assert.Equal(["none"], entry.P5.SubscriptionValues);
        Assert.Equal("result", entry.Result!.Type);
        Assert.Equal(["is_error", "terminal_reason"], entry.Result.SuccessWhen.Select(r => r.Path));
        Assert.Equal("rate_limit_info.resetsAt", entry.Usage!.ResetField);
        Assert.Equal("unix_seconds", entry.Usage.ResetFormat);
        Assert.Equal(2, entry.Usage.UtilizationFields.Length);
        Assert.Equal("rate_limit_info.isUsingOverage", entry.Usage.Overage.InUseField);
        Assert.Equal(JsonValueKind.False, entry.Usage.Overage.SafeInUse[0].ValueKind);
        Assert.Null(entry.Limit);
    }

    [Fact]
    public void For_2_1_309_GivesThe2_1_304BillingRules()
    {
        var entry = VersionRules.For("2.1.309");

        Assert.NotNull(entry);
        Assert.Equal(">=2.1.309 <2.1.310", entry.Versions);
        Assert.Equal(WithoutRangeOrLimit(VersionRules.For("2.1.304")!), WithoutRangeOrLimit(entry));
    }

    private static string WithoutRangeOrLimit(ClaudeRules rules) =>
        JsonSerializer.Serialize(rules with { Versions = "", Limit = null });

    [Fact]
    public void For_2_1_309_HasTheStreamLimitSignalFromTheRealCapture()
    {
        var entry = VersionRules.For("2.1.309")!;

        Assert.Equal(JsonSerializer.Serialize(TestLimits.Rule), JsonSerializer.Serialize(entry.Limit));
    }

    // The reset is the one in probes/e1/results/limit-2.1.309.json.
    [Fact]
    public void For_2_1_309_ClassifiesTheCapturedLimitWithItsReset()
    {
        var rules = VersionRules.For("2.1.309")!;
        var session = Guid.NewGuid();
        var id = session.ToString("D");
        var filter = new EventFilter(rules);
        var stream = new[]
        {
            $$"""{"type":"system","subtype":"init","session_id":"{{id}}","apiKeySource":"none"}""",
            TestLimits.RateLimitLine(id, "rejected", 1_792_769_400),
            TestLimits.ResultLine(id),
        }.Select(line => filter.Accept(line)!).ToList();

        var result = RunClassifier.Classify(rules, session, stream, [], 1);

        Assert.Equal(new Classification(Outcome.UsageLimit, RunClassifier.UsageLimitReason, 1_792_769_400), result);
    }

    private static string Entry(string versions) => $$"""{ "versions": "{{versions}}", "p2ExtraKeys": [] }""";

    [Fact]
    public void For_MatchesRange()
    {
        var rules = VersionRules.Load($"[{Entry(">=2.1.0 <2.2.0")}, {Entry(">=2.2.0 <3.0.0")}]");

        Assert.NotNull(VersionRules.For("2.1.304"));
        Assert.Equal(">=2.1.0 <2.2.0", rules.Match("2.1.999")!.Versions);
        Assert.Equal(">=2.2.0 <3.0.0", rules.Match("2.2.0")!.Versions);
    }

    [Theory]
    [InlineData("2.1.303")]
    [InlineData("2.1.305")]
    [InlineData("2.1.308")]
    [InlineData("2.1.310")]
    [InlineData("2.1")]
    [InlineData("2.1.304-beta")]
    [InlineData("")]
    public void For_UnknownVersion_ReturnsNull(string version)
    {
        Assert.Null(VersionRules.For(version));
    }

    [Theory]
    [InlineData("^2.1.304")]
    [InlineData(">=2.1.304")]
    [InlineData("<2.1.305 >=2.1.304")]
    [InlineData(">=2.1.305 <2.1.304")]
    public void Load_UnsupportedRange_Throws(string versions)
    {
        Assert.Throws<FormatException>(() => VersionRules.Load($"[{Entry(versions)}]"));
    }
}
