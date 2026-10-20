using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Tests.Classify;

public sealed class RunClassifierTests
{
    private static readonly Guid Session = Guid.Parse("9704e6d4-d947-4c1a-8224-b751c4def13f");

    private static readonly ClaudeRules Rules = VersionRules.LoadEmbedded().Entries[0];

    private static readonly HookEvent[] NoHooks = [];

    private static FilteredEvent Init() =>
        new("system", "init", Session.ToString(), null, "none", new Dictionary<string, string>());

    private static FilteredEvent Result(bool isError, string terminalReason, string subtype = "success") =>
        new("result", subtype, Session.ToString(), isError, null, new Dictionary<string, string>
        {
            ["is_error"] = isError ? "true" : "false",
            ["terminal_reason"] = $"\"{terminalReason}\"",
        });

    [Fact]
    public void SuccessResult_Completed()
    {
        var result = RunClassifier.Classify(Rules, Session, [Init(), Result(false, "completed")], NoHooks, 0);

        Assert.Equal(Outcome.Completed, result.Outcome);
        Assert.Null(result.ResetEpoch);
    }

    [Fact]
    public void SubtypeSuccessWithIsError_Failed()
    {
        var result = RunClassifier.Classify(Rules, Session, [Init(), Result(true, "api_error", subtype: "success")], NoHooks, 1);

        Assert.Equal(new Classification(Outcome.Failed, "Run failed", null), result);
    }

    [Fact]
    public void ErrorResult_Failed()
    {
        var result = RunClassifier.Classify(Rules, Session, [Init(), Result(true, "api_error", subtype: "error_during_execution")], NoHooks, 0);

        Assert.Equal(Outcome.Failed, result.Outcome);
    }

    [Fact]
    public void SuccessResultNonZeroExit_Failed()
    {
        var result = RunClassifier.Classify(Rules, Session, [Init(), Result(false, "completed")], NoHooks, 1);

        Assert.Equal(Outcome.Failed, result.Outcome);
    }

    [Fact]
    public void NoResultEvent_Failed()
    {
        var result = RunClassifier.Classify(Rules, Session, [Init()], NoHooks, 0);

        Assert.Equal(Outcome.Failed, result.Outcome);
    }

    private static readonly ClaudeRules LimitRules = Rules with
    {
        Limit = new LimitSignalRule("StopFailure", "error", ["rate_limit"], "resets_at", JsonLiteral.UnixSeconds),
    };

    private static HookEvent Hook(string name, Guid session, string reason, long? resetsAt = null)
    {
        var fields = new Dictionary<string, string> { ["error"] = $"\"{reason}\"" };
        if (resetsAt is { } reset)
        {
            fields["resets_at"] = reset.ToString();
        }
        return new HookEvent(name, session.ToString(), fields);
    }

    private static FilteredEvent Usage(long resetsAt) =>
        new("rate_limit_event", null, Session.ToString(), null, null, new Dictionary<string, string>
        {
            ["rate_limit_info.resetsAt"] = resetsAt.ToString(),
        });

    [Fact]
    public void VerifiedLimit_UsageLimitWithResetEpoch()
    {
        var result = RunClassifier.Classify(
            LimitRules,
            Session,
            [Init(), Usage(1_700_000_000), Result(true, "api_error")],
            [Hook("StopFailure", Session, "rate_limit", resetsAt: 1_800_000_000)],
            1);

        Assert.Equal(new Classification(Outcome.UsageLimit, "Usage limit", 1_800_000_000), result);
    }

    [Fact]
    public void VerifiedLimitWithoutHookReset_ResetFromLastUsageEvent()
    {
        var result = RunClassifier.Classify(
            LimitRules,
            Session,
            [Init(), Usage(1_700_000_000), Usage(1_800_000_000)],
            [Hook("StopFailure", Session, "rate_limit")],
            1);

        Assert.Equal(new Classification(Outcome.UsageLimit, "Usage limit", 1_800_000_000), result);
    }

    [Fact]
    public void VerifiedLimitWithoutAnyReset_NullEpoch()
    {
        var result = RunClassifier.Classify(LimitRules, Session, [Init()], [Hook("StopFailure", Session, "rate_limit")], 0);

        Assert.Equal(new Classification(Outcome.UsageLimit, "Usage limit", null), result);
    }

    private static readonly Classification Ambiguous = new(Outcome.Ambiguous, "Stopped, reason unverified", null);

    [Fact]
    public void LimitWithWrongSessionId_Ambiguous()
    {
        var result = RunClassifier.Classify(LimitRules, Session, [Init()], [Hook("StopFailure", Guid.NewGuid(), "rate_limit", 1_800_000_000)], 1);

        Assert.Equal(Ambiguous, result);
    }

    [Fact]
    public void LimitWithNoRule_Ambiguous()
    {
        var result = RunClassifier.Classify(Rules, Session, [Init()], [Hook("StopFailure", Session, "rate_limit", 1_800_000_000)], 1);

        Assert.Equal(Ambiguous, result);
    }

    [Fact]
    public void LimitWithOtherReason_Ambiguous()
    {
        var result = RunClassifier.Classify(LimitRules, Session, [Init()], [Hook("StopFailure", Session, "server_error")], 1);

        Assert.Equal(Ambiguous, result);
    }

    [Fact]
    public void HookErrorOnly_Ambiguous()
    {
        var hookError = new HookEvent("hook_error", null, new Dictionary<string, string>());

        var result = RunClassifier.Classify(LimitRules, Session, [Init()], [hookError], 1);

        Assert.Equal(Ambiguous, result);
    }

    [Fact]
    public void ApiKeyAuthSource_BillingMismatchOverridesSuccess()
    {
        var apiKeyInit = Init() with { AuthSource = "ANTHROPIC_API_KEY" };

        var result = RunClassifier.Classify(Rules, Session, [apiKeyInit, Result(false, "completed")], NoHooks, 0);

        Assert.Equal(new Classification(Outcome.BillingMismatch, "Billing unverified", null), result);
    }

    [Fact]
    public void OutputBeforeInit_BillingMismatchOverridesLimit()
    {
        var result = RunClassifier.Classify(
            LimitRules,
            Session,
            [Result(true, "api_error"), Init()],
            [Hook("StopFailure", Session, "rate_limit", 1_800_000_000)],
            1);

        Assert.Equal(Outcome.BillingMismatch, result.Outcome);
    }

    [Fact]
    public void NoInitAndNoOutput_ClassifiedNormally()
    {
        var result = RunClassifier.Classify(Rules, Session, [], NoHooks, 1);

        Assert.Equal(Outcome.Failed, result.Outcome);
    }

    [Fact]
    public void NullRules_Ambiguous()
    {
        var result = RunClassifier.Classify(null, Session, [Init(), Result(false, "completed")], NoHooks, 0);

        Assert.Equal(Ambiguous, result);
    }
}
