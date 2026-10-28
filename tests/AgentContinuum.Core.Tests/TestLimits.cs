using System.Text.Json;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Tests;

/// <summary>The stream limit signal Claude Code 2.1.309 gave at a real usage limit, as a rule and as stream lines.</summary>
public static class TestLimits
{
    public static readonly LimitSignalRule Rule = new(
        [
            new LimitEventRule("result", [Field("api_error", "\"usage_limit_reached\""), Field("api_error_status", "429")]),
            new LimitEventRule("rate_limit_event", [Field("rate_limit_info.status", "\"rejected\"")]),
        ],
        "rate_limit_event",
        "rate_limit_info.resetsAt",
        JsonLiteral.UnixSeconds);

    public static string RateLimitLine(string session, string status, long resetsAt) =>
        $$"""{"type":"rate_limit_event","rate_limit_info":{"status":"{{status}}","resetsAt":{{resetsAt}},"rateLimitType":"five_hour"},"session_id":"{{session}}"}""";

    public static string ResultLine(string session) =>
        $$"""{"type":"result","subtype":"success","is_error":true,"terminal_reason":"api_error","api_error_status":429,"api_error":"usage_limit_reached","result":"limit text","session_id":"{{session}}"}""";

    private static FieldRequirement Field(string path, string literal)
    {
        using var doc = JsonDocument.Parse(literal);
        return new FieldRequirement(path, [doc.RootElement.Clone()]);
    }
}
