namespace AgentContinuum.Core.Preflight.Checks;

/// <summary>`ac resume --now` waives only this check; the launch still counts as an attempt.</summary>
internal sealed class EligibilityCheck : IPreflightCheck
{
    public IEnumerable<string> Reasons(PreflightContext ctx)
    {
        if (!ctx.Now
            && ctx.Task.AttemptPermittedAtEpoch is { } permitted
            && ctx.UtcNow < DateTimeOffset.FromUnixTimeSeconds(permitted))
        {
            yield return PreflightReasons.NotYetEligible;
        }
    }
}
