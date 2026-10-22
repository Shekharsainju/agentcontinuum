using AgentContinuum.Cli;
using AgentContinuum.Core.Model;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>AC15: a 2-hour clock jump in either direction gives at most one offer.</summary>
public sealed class AC15ClockJumpTests
{
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    // Enough daemon ticks at 0.5 s for a jump to be seen and acted on.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData("forward")]
    [InlineData("backward")]
    public void AC15_ClockJumpOneOffer(string direction)
    {
        using var root = new AcceptanceRoot(attest: false);
        var start = direction == "forward" ? TimeSpan.Zero : -TwoHours;
        root.SetClockOffset(start);
        AcceptanceRoot.Ok(root.Ac("attest-overage-off"));
        var worktree = root.Worktree(AcceptanceRoot.LimitScenario(root.Clock.UtcNow.AddHours(1).ToUnixTimeSeconds()));
        var taskId = root.AddTask(worktree);
        root.StartDaemon();

        var reply = root.Reply(root.Submit(RequestKinds.Start, taskId), TimeSpan.FromSeconds(60))!;
        Assert.True(reply["launched"]!.GetValue<bool>(), reply.ToJsonString());
        AcceptanceRoot.Eventually(
            () => root.Task(taskId).State == TaskState.WaitingForReset, TimeSpan.FromSeconds(60), "the wait for the reset");
        Thread.Sleep(Settle);
        Assert.Empty(Offers(root, taskId));

        TimeSpan[] jumps = direction == "forward"
            ? [TwoHours, TimeSpan.Zero, TwoHours]
            : [TimeSpan.Zero, -TwoHours, TimeSpan.Zero];
        foreach (var offset in jumps)
        {
            root.SetClockOffset(offset);
            Thread.Sleep(Settle);
        }

        Assert.Single(Offers(root, taskId));
        Assert.Equal(TaskState.ResumeOffered, root.Task(taskId).State);
        Assert.Contains(root.LogLines(), line => line.Contains("clock jump", StringComparison.Ordinal));
    }

    private static string[] Offers(AcceptanceRoot root, string taskId) =>
        root.Notifications().Where(line => line.StartsWith($"offer\t{taskId}\t", StringComparison.Ordinal)).ToArray();
}
