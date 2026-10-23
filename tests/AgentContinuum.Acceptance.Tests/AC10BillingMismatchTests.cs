using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentContinuum.Cli;
using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Model;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>AC10: a P5 mismatch on the first event terminates the run.</summary>
public sealed class AC10BillingMismatchTests : IDisposable
{
    private readonly AcceptanceRoot _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void AC10_P5MismatchTerminates()
    {
        var worktree = _root.Worktree(new JsonObject
        {
            ["authSource"] = "ANTHROPIC_API_KEY",
            ["events"] = new JsonArray(),
            ["lingerSeconds"] = 60,
            ["exitCode"] = 0,
        });
        var taskId = _root.AddTask(worktree);
        _root.StartDaemon();
        var watch = Stopwatch.StartNew();

        var reply = _root.Reply(_root.Submit(RequestKinds.Start, taskId), TimeSpan.FromSeconds(60))!;
        Assert.True(reply["launched"]!.GetValue<bool>(), reply.ToJsonString());
        AcceptanceRoot.Eventually(
            () => _root.Task(taskId).State == TaskState.NeedsAttention, TimeSpan.FromSeconds(30), "attention after the mismatch");

        // Well inside the 60 s the run would otherwise have lingered.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(RunClassifier.BillingMismatchReason, _root.Task(taskId).AttentionReason);
        var op = Assert.Single(_root.Ops(taskId));
        Assert.Equal((OpState.Classified, Outcome.BillingMismatch), (op.State, op.Outcome));
        Assert.True(AcceptanceRoot.Gone(op.Pid!.Value));
        Assert.Contains($"attention\t{taskId}\tacceptance task needs attention\t{RunClassifier.BillingMismatchReason}", _root.Notifications());
    }
}
