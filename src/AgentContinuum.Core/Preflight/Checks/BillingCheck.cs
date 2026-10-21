using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Preflight.Checks;

internal sealed class BillingCheck : IPreflightCheck
{
    private static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();

    private readonly BillingProofs _billing;
    private readonly PreflightHost _host;

    public BillingCheck(BillingProofs billing, PreflightHost host)
    {
        _billing = billing;
        _host = host;
    }

    public IEnumerable<string> Reasons(PreflightContext ctx)
    {
        var rules = VersionRules.For(ctx.Task.CliVersion);
        // P4 runs the CLI. Withholding its rule leaves it unevaluable, so an unpinned binary or one with no
        // allowlisted environment is never run.
        if (rules is not null && (ctx.CliPinProblem is not null || ctx.ChildEnv is null))
        {
            rules = rules with { P4 = null };
        }
        var verdict = _billing.Evaluate(new BillingContext(ctx.Task, rules, ctx.ChildEnv ?? NoEnv, _host.UserProfile, _host.ManagedSettingsPaths));
        if (verdict.Verified)
        {
            return [];
        }
        return [$"{PreflightReasons.BillingUnverified}: {string.Join(", ", verdict.FailedProofs)}"];
    }
}
