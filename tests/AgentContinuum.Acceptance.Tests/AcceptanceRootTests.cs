using AgentContinuum.Cli;
using AgentContinuum.Core.Model;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>The harness itself: a plain launch through ac-test.exe and FakeClaude completes.</summary>
public sealed class AcceptanceRootTests : IDisposable
{
    private readonly AcceptanceRoot _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void FirstLaunch_Completes()
    {
        var worktree = _root.Worktree(AcceptanceRoot.SuccessScenario());
        var taskId = _root.AddTask(worktree);
        _root.StartDaemon();

        var reply = _root.Reply(_root.Submit(RequestKinds.Start, taskId), TimeSpan.FromSeconds(60));

        Assert.True(reply?["launched"]?.GetValue<bool>() == true, reply?.ToJsonString() ?? "no reply");
        AcceptanceRoot.Eventually(() => _root.Task(taskId).State == TaskState.Completed, TimeSpan.FromSeconds(60), "completion");
        Assert.Equal(1, AcceptanceRoot.StartsLogged(worktree));
    }
}
