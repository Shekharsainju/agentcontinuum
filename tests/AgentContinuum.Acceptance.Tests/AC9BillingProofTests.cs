using System.Text.Json.Nodes;
using AgentContinuum.Cli;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>AC9: each of P2 to P6 failing alone blocks the launch.</summary>
public sealed class AC9BillingProofTests
{
    public static TheoryData<string> Proofs() => new() { "none", "P2", "P3", "P4", "P5", "P6" };

    [Theory]
    [MemberData(nameof(Proofs))]
    public void AC9_EachProofAloneBlocks(string proof)
    {
        using var root = new AcceptanceRoot(attest: proof != "P6");
        var worktree = root.Worktree(AcceptanceRoot.SuccessScenario());
        var taskId = root.AddTask(worktree);
        Break(root, proof);
        root.StartDaemon();

        var reply = root.Reply(root.Submit(RequestKinds.Start, taskId), TimeSpan.FromSeconds(60))!;

        if (proof == "none")
        {
            Assert.True(reply["launched"]!.GetValue<bool>(), reply.ToJsonString());
            return;
        }
        Assert.False(reply["launched"]!.GetValue<bool>());
        var expected = proof == "P5" ? "P5_UNEVALUABLE" : proof;
        Assert.Equal([$"BILLING_UNVERIFIED: {expected}"], reply["reasons"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(0, root.Task(taskId).AttemptsUsed);
        Assert.Empty(root.Ops(taskId));
        Assert.Equal(0, AcceptanceRoot.StartsLogged(worktree));
    }

    private static void Break(AcceptanceRoot root, string proof)
    {
        switch (proof)
        {
            case "P2":
                File.WriteAllText(
                    Path.Combine(root.Profile, ".claude", "settings.json"),
                    """{"env":{"ANTHROPIC_API_KEY":"sk-test"}}""");
                break;
            case "P3":
                File.Delete(Path.Combine(root.Profile, ".claude", ".credentials.json"));
                break;
            case "P4":
                File.WriteAllText(
                    Path.Combine(root.Profile, ".fake-claude-auth.json"),
                    """{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty","subscriptionType":null}""");
                break;
            case "P5":
                root.EditRules(rules => rules.Remove("p5"));
                break;
        }
    }
}
