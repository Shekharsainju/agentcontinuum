using AgentContinuum.Core.Platform;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Billing;

/// <summary>R15: a launch is VERIFIED-SUBSCRIPTION only when every proof passes; one that cannot run fails.</summary>
public sealed class BillingProofs
{
    private readonly ICommandRunner _runner;
    private readonly IClock _clock;
    private readonly StateStore _store;

    public BillingProofs(ICommandRunner runner, IClock clock, StateStore store)
    {
        _runner = runner;
        _clock = clock;
        _store = store;
    }

    public BillingVerdict Evaluate(BillingContext ctx)
    {
        var failed = new List<string>();
        if (!ChildEnvironment.IsAllowlisted(ctx.ChildEnv))
        {
            failed.Add("P1");
        }
        if (!SettingsScanner.Scan(ctx.UserProfile, ctx.Task.Worktree, ctx.ManagedSettingsPath, ctx.Rules).Pass)
        {
            failed.Add("P2");
        }
        if (ctx.Rules?.P3 is not { } p3)
        {
            failed.Add("P3_UNEVALUABLE");
        }
        else if (!CredentialCheck.Check(ctx.UserProfile, p3).Pass)
        {
            failed.Add("P3");
        }
        return new BillingVerdict(failed.Count == 0, failed);
    }
}
