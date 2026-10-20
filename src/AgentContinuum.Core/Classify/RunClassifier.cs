using AgentContinuum.Core.Events;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Classify;

public static class RunClassifier
{
    public const string CompletedReason = "Completed";
    public const string UsageLimitReason = "Usage limit";
    public const string FailedReason = "Run failed";
    public const string AmbiguousReason = "Stopped, reason unverified";

    public static Classification Classify(
        ClaudeRules? rules,
        Guid sessionId,
        IReadOnlyList<FilteredEvent> stream,
        IReadOnlyList<HookEvent> hooks,
        int exitCode)
    {
        if (exitCode == 0 && rules?.Result is { } resultRule && IsSuccess(resultRule, stream))
        {
            return new Classification(Outcome.Completed, CompletedReason, null);
        }

        if (rules?.Limit is { } limitRule && hooks.LastOrDefault(h => IsVerifiedLimit(limitRule, sessionId, h)) is { } limit)
        {
            return new Classification(Outcome.UsageLimit, UsageLimitReason, ResetEpoch(rules, limitRule, limit, stream));
        }

        // With no rules nothing about the run can be verified. A hook only fires on a stop, so one the limit rule
        // did not confirm is unverified, not a plain failure.
        if (rules is null || hooks.Count > 0)
        {
            return new Classification(Outcome.Ambiguous, AmbiguousReason, null);
        }

        return new Classification(Outcome.Failed, FailedReason, null);
    }

    // subtype alone never decides success: E1 saw a 401 run report subtype "success".
    private static bool IsSuccess(ResultRule rule, IReadOnlyList<FilteredEvent> stream)
    {
        var result = stream.LastOrDefault(e => e.Type == rule.Type);
        return result is not null
            && rule.SuccessWhen.Length > 0
            && rule.SuccessWhen.All(r => JsonLiteral.MatchesAny(result.LimitFields.GetValueOrDefault(r.Path), r.Values));
    }

    private static bool IsVerifiedLimit(LimitSignalRule rule, Guid sessionId, HookEvent hook) =>
        hook.HookName == rule.Hook
        && Guid.TryParse(hook.SessionId, out var hookSession)
        && hookSession == sessionId
        && rule.ReasonValues.Contains(JsonLiteral.AsString(hook.Fields.GetValueOrDefault(rule.ReasonField)));

    private static long? ResetEpoch(ClaudeRules rules, LimitSignalRule limitRule, HookEvent limit, IReadOnlyList<FilteredEvent> stream)
    {
        if (limitRule.ResetField is { } resetField
            && JsonLiteral.AsEpoch(limit.Fields.GetValueOrDefault(resetField), limitRule.ResetFormat) is { } fromHook)
        {
            return fromHook;
        }

        if (rules.Usage is { } usageRule && stream.LastOrDefault(e => e.Type == usageRule.Type) is { } usage)
        {
            return JsonLiteral.AsEpoch(usage.LimitFields.GetValueOrDefault(usageRule.ResetField), usageRule.ResetFormat);
        }

        return null;
    }
}
