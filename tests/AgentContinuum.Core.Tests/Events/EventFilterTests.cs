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

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{\"subtype\":\"init\"}")]
    [InlineData("{\"type\":1}")]
    [InlineData("{\"type\":\"result\"")]
    [InlineData("{\"type\":\"result\"} trailing")]
    [InlineData("{\"type\":\"result\"}{\"type\":\"result\"}")]
    public void Accept_UnparseableLine_ReturnsNull(string line)
    {
        Assert.Null(Filter().Accept(line));
    }

    private static string ResultLineOfBytes(int totalBytes, char pad)
    {
        const string head = "{\"type\":\"result\",\"is_error\":false,\"result\":\"";
        const string tail = "\"}";
        var padBytes = System.Text.Encoding.UTF8.GetByteCount(pad.ToString());
        var padCount = (totalBytes - head.Length - tail.Length) / padBytes;
        var line = head + new string(pad, padCount) + tail;
        Assert.Equal(totalBytes, System.Text.Encoding.UTF8.GetByteCount(line));
        return line;
    }

    [Fact]
    public void Accept_LineOver1MB_ReturnsNull()
    {
        var filter = Filter();

        Assert.NotNull(filter.Accept(ResultLineOfBytes(1024 * 1024, 'x')));
        Assert.Null(filter.Accept(ResultLineOfBytes(1024 * 1024 + 1, 'x')));
        Assert.Null(filter.Accept(ResultLineOfBytes(1024 * 1024 + 2, '\u00fc')));
    }

    [Fact]
    public void Accept_CapsValuesAt200Chars()
    {
        var longText = new string('a', 500);
        var longNumber = new string('9', 500);
        var line = $$$"""
            {"type":"{{{longText}}}","subtype":"{{{longText}}}","session_id":"{{{longText}}}","terminal_reason":"{{{longText}}}","rate_limit_info":{"resetsAt":{{{longNumber}}}}}
            """;

        var filtered = Filter().Accept(line)!;

        Assert.Equal(200, filtered.Type.Length);
        Assert.Equal(200, filtered.Subtype!.Length);
        Assert.Equal(200, filtered.SessionId!.Length);
        Assert.Equal(new string('a', 200), JsonSerializer.Deserialize<string>(filtered.LimitFields["terminal_reason"]));
        Assert.Equal(200, filtered.LimitFields["rate_limit_info.resetsAt"].Length);
    }
}
