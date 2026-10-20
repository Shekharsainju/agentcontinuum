using System.Text.Json;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Billing;

/// <summary>R15: a launch is VERIFIED-SUBSCRIPTION only when every proof passes; one that cannot run fails.</summary>
public sealed class BillingProofs
{
    public static readonly TimeSpan AuthStatusTimeout = TimeSpan.FromSeconds(15);

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
        if (ctx.Rules?.P4 is not { } p4)
        {
            failed.Add("P4_UNEVALUABLE");
        }
        else if (!AuthStatusReportsSubscription(ctx, p4))
        {
            failed.Add("P4");
        }
        // P5 itself is judged on the running stream; before launch the rule only has to exist.
        if (ctx.Rules?.P5 is null)
        {
            failed.Add("P5_UNEVALUABLE");
        }
        return new BillingVerdict(failed.Count == 0, failed);
    }

    // The output names the account, so it is parsed here and never stored or logged.
    private bool AuthStatusReportsSubscription(BillingContext ctx, AuthStatusRule rule)
    {
        CommandResult result;
        try
        {
            result = _runner.Run(ctx.Task.CliPath, rule.Args, ctx.ChildEnv, AuthStatusTimeout);
        }
        catch (Exception)
        {
            return false;
        }
        if (result.TimedOut || result.ExitCode != 0)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && rule.Require.All(r => JsonPaths.TryGet(root, r.Path, out var value) && JsonLiteral.MatchesAny(value.GetRawText(), r.Values))
                && rule.RequirePresent.All(path => JsonPaths.Has(root, path));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
