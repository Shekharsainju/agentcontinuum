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
    public static readonly TimeSpan AttestationMaxAge = TimeSpan.FromDays(30);

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
        if (!ChildEnvironment.IsAllowlisted(ctx.ChildEnv) || !ChildUsesScannedProfile(ctx))
        {
            failed.Add("P1");
        }
        if (!SettingsScanner.Scan(ctx.UserProfile, ctx.Task.Worktree, ctx.ManagedSettingsPaths, ctx.Rules).Pass)
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
        if (!OverageAttestationCurrent())
        {
            failed.Add("P6");
        }
        return new BillingVerdict(failed.Count == 0, failed);
    }

    // Claude Code reads user settings and credentials from the child's USERPROFILE, so P2 and P3 only prove anything
    // when they scanned that same directory.
    private static bool ChildUsesScannedProfile(BillingContext ctx)
    {
        var childProfile = ctx.ChildEnv
            .FirstOrDefault(kv => kv.Key.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase)).Value;
        // A relative profile resolves against the child's working directory, not ours, so the comparison means nothing.
        if (string.IsNullOrEmpty(childProfile) || !Path.IsPathFullyQualified(childProfile) || !Path.IsPathFullyQualified(ctx.UserProfile))
        {
            return false;
        }
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(childProfile)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(ctx.UserProfile)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // An attestation dated in the future fails too: the wall clock cannot be trusted to age it.
    private bool OverageAttestationCurrent()
    {
        if (new Attestations(_store).Latest(Attestations.OverageOff) is not { } at)
        {
            return false;
        }
        var age = _clock.UtcNow - at;
        return age >= TimeSpan.Zero && age < AttestationMaxAge;
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
