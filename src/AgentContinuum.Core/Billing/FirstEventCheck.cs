using AgentContinuum.Core.Events;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Billing;

public enum FirstEventStatus
{
    Pending,
    Verified,
    Mismatch,
}

/// <summary>P5: the event named in the version rules reports a subscription auth source before any model output.</summary>
public static class FirstEventCheck
{
    // Model output or a final result before the P5 event means work ran on an unproven route.
    private static readonly string[] OutputTypes = ["assistant", "result"];

    public static bool Matches(FilteredEvent e, ClaudeRules rules) =>
        rules.P5 is { } p5
        && IsP5Event(e, p5)
        && e.AuthSource is { } source
        && p5.SubscriptionValues.Contains(source, StringComparer.Ordinal);

    public static FirstEventStatus Observe(FirstEventStatus current, FilteredEvent e, ClaudeRules? rules)
    {
        if (current == FirstEventStatus.Mismatch)
        {
            return current;
        }
        // Settings can reload mid-run; a later P5 event off the subscription is evidence the route changed.
        if (current == FirstEventStatus.Verified)
        {
            return rules?.P5 is { } later && IsP5Event(e, later) && !Matches(e, rules) ? FirstEventStatus.Mismatch : current;
        }
        if (rules?.P5 is not { } p5)
        {
            return FirstEventStatus.Mismatch;
        }
        if (IsP5Event(e, p5))
        {
            return Matches(e, rules) ? FirstEventStatus.Verified : FirstEventStatus.Mismatch;
        }
        return OutputTypes.Contains(e.Type, StringComparer.Ordinal) ? FirstEventStatus.Mismatch : FirstEventStatus.Pending;
    }

    public static FirstEventStatus Over(IEnumerable<FilteredEvent> stream, ClaudeRules? rules) =>
        stream.Aggregate(FirstEventStatus.Pending, (status, e) => Observe(status, e, rules));

    private static bool IsP5Event(FilteredEvent e, FirstEventRule p5) => e.Type == p5.Type && e.Subtype == p5.Subtype;
}
