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

    private static readonly ClaudeRules LimitRules = Rules with { Limit = TestLimits.Rule };

    private static FilteredEvent LimitResult(Guid? session = null, string apiError = "usage_limit_reached", int status = 429) =>
        new("result", "success", (session ?? Session).ToString(), true, null, new Dictionary<string, string>
        {
            ["is_error"] = "true",
            ["terminal_reason"] = "\"api_error\"",
            ["api_error"] = $"\"{apiError}\"",
            ["api_error_status"] = status.ToString(),
        });

    private static FilteredEvent RateLimit(string status, long? resetsAt = null, Guid? session = null)
    {
        var fields = new Dictionary<string, string> { ["rate_limit_info.status"] = $"\"{status}\"" };
        if (resetsAt is { } reset)
        {
            fields["rate_limit_info.resetsAt"] = reset.ToString();
        }
        return new FilteredEvent("rate_limit_event", null, (session ?? Session).ToString(), null, null, fields);
    }

    [Fact]
    public void VerifiedLimit_UsageLimitWithResetEpoch()
    {
        var result = RunClassifier.Classify(
            LimitRules, Session, [Init(), RateLimit("rejected", 1_800_000_000), LimitResult()], NoHooks, 1);

        Assert.Equal(new Classification(Outcome.UsageLimit, "Usage limit", 1_800_000_000), result);
    }

    [Fact]
    public void VerifiedLimit_ResetFromLastRateLimitEvent()
    {
        var result = RunClassifier.Classify(
            LimitRules,
            Session,
            [Init(), RateLimit("allowed", 1_700_000_000), RateLimit("rejected", 1_800_000_000), LimitResult()],
            NoHooks,
            1);

        Assert.Equal(new Classification(Outcome.UsageLimit, "Usage limit", 1_800_000_000), result);
    }

    [Fact]
    public void VerifiedLimitWithoutReset_NullEpoch()
    {
        var result = RunClassifier.Classify(LimitRules, Session, [Init(), RateLimit("rejected"), LimitResult()], NoHooks, 1);

        Assert.Equal(new Classification(Outcome.UsageLimit, "Usage limit", null), result);
    }

    private static readonly Classification Ambiguous = new(Outcome.Ambiguous, "Stopped, reason unverified", null);

    [Fact]
    public void LimitResultWithoutRejectedRateLimit_Ambiguous()
    {
        var result = RunClassifier.Classify(
            LimitRules, Session, [Init(), RateLimit("allowed", 1_800_000_000), LimitResult()], NoHooks, 1);

        Assert.Equal(Ambiguous, result);
    }

    [Fact]
    public void RejectedRateLimitWithoutLimitResult_Ambiguous()
    {
        var result = RunClassifier.Classify(
            LimitRules, Session, [Init(), RateLimit("rejected", 1_800_000_000), Result(true, "api_error")], NoHooks, 1);

        Assert.Equal(Ambiguous, result);
    }

    [Fact]
    public void LimitWithWrongSessionId_Ambiguous()
    {
        var other = Guid.NewGuid();

        var result = RunClassifier.Classify(
            LimitRules, Session, [Init(), RateLimit("rejected", 1_800_000_000, other), LimitResult(other)], NoHooks, 1);

        Assert.Equal(Ambiguous, result);
    }

    [Fact]
    public void OtherApiError_Failed()
    {
        var result = RunClassifier.Classify(
            LimitRules,
            Session,
            [Init(), RateLimit("allowed", 1_800_000_000), LimitResult(apiError: "overloaded_error", status: 529)],
            NoHooks,
            1);

        Assert.Equal(new Classification(Outcome.Failed, "Run failed", null), result);
    }

    [Fact]
    public void LimitWithNoRule_Failed()
    {
        var result = RunClassifier.Classify(
            Rules, Session, [Init(), RateLimit("rejected", 1_800_000_000), LimitResult()], NoHooks, 1);

        Assert.Equal(new Classification(Outcome.Failed, "Run failed", null), result);
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
            LimitRules, Session, [LimitResult(), RateLimit("rejected", 1_800_000_000), Init()], NoHooks, 1);

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
