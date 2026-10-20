using AgentContinuum.Core.Events;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Classify;

public static class RunClassifier
{
    public const string CompletedReason = "Completed";
    public const string FailedReason = "Run failed";

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
}
