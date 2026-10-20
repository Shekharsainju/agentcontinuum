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
}
