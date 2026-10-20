using System.Text.Json;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Tests.Events;

public sealed class EventFilterTests
{
    private const string Sentinel = "SENTINEL-c0ffee";
    private const string SessionId = "9704e6d4-d947-4c1a-8224-b751c4def13f";

    private const string InitLine = $$$"""
        {"type":"system","subtype":"init","cwd":"C:\\work\\{{{Sentinel}}}","session_id":"{{{SessionId}}}","tools":["Task","{{{Sentinel}}}"],"mcp_servers":[{"name":"{{{Sentinel}}}","status":"connected"}],"model":"claude-opus-5-5","permissionMode":"auto","apiKeySource":"none","{{{Sentinel}}}":"{{{Sentinel}}}"}
        """;

    private const string AssistantLine = $$$"""
        {"type":"assistant","message":{"type":"{{{Sentinel}}}","content":[{"type":"text","text":"{{{Sentinel}}}"}]},"session_id":"{{{SessionId}}}"}
        """;

    private const string ResultLine = $$$"""
        {"type":"result","subtype":"success","is_error":false,"terminal_reason":"completed","result":"{{{Sentinel}}}","session_id":"{{{SessionId}}}","total_cost_usd":0,"usage":{"input_tokens":3}}
        """;

    private const string RateLimitLine = $$$"""
        {"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1800000000,"unifiedWindows":{"five_hour":{"utilization":0.42,"resetsAt":1800000000,"note":"{{{Sentinel}}}"},"seven_day":{"utilization":0.1}},"overageStatus":"rejected","isUsingOverage":false,"overageDisabledReason":"org_level_disabled"},"session_id":"{{{SessionId}}}"}
        """;

    private static EventFilter Filter() => new(VersionRules.LoadEmbedded().Entries[0]);

    [Fact]
    public void Accept_KeepsOnlyAllowlistedFields()
    {
        var filter = Filter();

        var init = filter.Accept(InitLine)!;
        var result = filter.Accept(ResultLine)!;
        var rate = filter.Accept(RateLimitLine)!;

        Assert.Equal(("system", "init", SessionId, "none"), (init.Type, init.Subtype, init.SessionId, init.AuthSource));
        Assert.Null(init.IsError);
        Assert.Empty(init.LimitFields);

        Assert.Equal(("result", "success", false), (result.Type, result.Subtype, result.IsError));
        Assert.Equal(
            new Dictionary<string, string> { ["is_error"] = "false", ["terminal_reason"] = "\"completed\"" },
            result.LimitFields);

        Assert.Equal("rate_limit_event", rate.Type);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["rate_limit_info.resetsAt"] = "1800000000",
                ["rate_limit_info.unifiedWindows.five_hour.utilization"] = "0.42",
                ["rate_limit_info.unifiedWindows.seven_day.utilization"] = "0.1",
                ["rate_limit_info.overageStatus"] = "\"rejected\"",
                ["rate_limit_info.isUsingOverage"] = "false",
            },
            rate.LimitFields);
    }

    [Fact]
    public void Accept_DropsSentinelEverywhere()
    {
        var filter = Filter();

        foreach (var line in new[] { InitLine, AssistantLine, ResultLine, RateLimitLine })
        {
            var filtered = filter.Accept(line);

            Assert.NotNull(filtered);
            Assert.DoesNotContain(Sentinel, JsonSerializer.Serialize(filtered));
        }
    }
}
