using AgentContinuum.Core.Billing;
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
    public const string BillingMismatchReason = "Billing unverified";

    public static Classification Classify(
        ClaudeRules? rules,
        Guid sessionId,
        IReadOnlyList<FilteredEvent> stream,
        IReadOnlyList<HookEvent> hooks,
        int exitCode)
    {
        // A billing mismatch outranks every other outcome: the run may have been billed off the subscription.
        if (rules?.P5 is not null && FirstEventCheck.Over(stream, rules) == FirstEventStatus.Mismatch)
        {
            return new Classification(Outcome.BillingMismatch, BillingMismatchReason, null);
        }

        if (exitCode == 0 && rules?.Result is { } resultRule && IsSuccess(resultRule, stream))
        {
            return new Classification(Outcome.Completed, CompletedReason, null);
        }

        if (rules?.Limit is { } limitRule && IsVerifiedLimit(limitRule, sessionId, stream))
        {
            return new Classification(Outcome.UsageLimit, UsageLimitReason, ResetEpoch(limitRule, sessionId, stream));
        }

        // With no rules nothing about the run can be verified. Launches no longer install a hook, but an op started
        // by an earlier build may have recorded one, and a hook only fired on a stop. A stop that matches part of the
        // limit rule looks like a limit. Either is unverified rather than a plain failure.
        if (rules is null || hooks.Count > 0 || (rules.Limit is { } partial && IsLimitLike(partial, stream)))
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

    public static bool IsVerifiedLimit(LimitSignalRule rule, Guid sessionId, IReadOnlyList<FilteredEvent> stream) =>
        rule.Events.Length > 0
        && rule.Events.All(r => stream.Any(e => IsOwnSession(e, sessionId) && Matches(r, e)));

    private static bool IsLimitLike(LimitSignalRule rule, IReadOnlyList<FilteredEvent> stream) =>
        rule.Events.Any(r => stream.Any(e => Matches(r, e)));

    private static bool Matches(LimitEventRule rule, FilteredEvent e) =>
        e.Type == rule.Type
        && rule.When.Length > 0
        && rule.When.All(r => JsonLiteral.MatchesAny(e.LimitFields.GetValueOrDefault(r.Path), r.Values));

    private static bool IsOwnSession(FilteredEvent e, Guid sessionId) =>
        Guid.TryParse(e.SessionId, out var session) && session == sessionId;

    // A later event for another window can carry an earlier reset, and resuming at it would hit the limit again.
    private static long? ResetEpoch(LimitSignalRule rule, Guid sessionId, IReadOnlyList<FilteredEvent> stream)
    {
        var candidates = stream.Where(e => e.Type == rule.ResetType && IsOwnSession(e, sessionId)).ToList();
        var limiting = candidates.Where(e => rule.Events.Any(r => r.Type == rule.ResetType && Matches(r, e)));
        return LastEpoch(rule, limiting) ?? LastEpoch(rule, candidates);
    }

    private static long? LastEpoch(LimitSignalRule rule, IEnumerable<FilteredEvent> events) =>
        events
            .Select(e => JsonLiteral.AsEpoch(e.LimitFields.GetValueOrDefault(rule.ResetField), rule.ResetFormat))
            .LastOrDefault(epoch => epoch is not null);
}
