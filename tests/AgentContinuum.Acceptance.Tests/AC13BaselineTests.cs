using AgentContinuum.Cli;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Tests;
using AgentContinuum.Core.Workspace;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>AC13: the baseline is never taken while the job has processes.</summary>
public sealed class AC13BaselineTests : IDisposable
{
    private readonly AcceptanceRoot _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void AC13_NoBaselineWithActiveProcesses()
    {
        var scenario = AcceptanceRoot.LimitScenario(_root.Clock.UtcNow.AddHours(1).ToUnixTimeSeconds(), spawnChildren: 2);
        scenario["childLingerSeconds"] = 3;
        scenario["childTouchFile"] = "child-done.txt";
        var worktree = _root.Worktree(scenario);
        var taskId = _root.AddTask(worktree);
        _root.StartDaemon();

        var reply = _root.Reply(_root.Submit(RequestKinds.Start, taskId), TimeSpan.FromSeconds(60))!;
        Assert.True(reply["launched"]!.GetValue<bool>(), reply.ToJsonString());
        AcceptanceRoot.Eventually(
            () => _root.Task(taskId).State == TaskState.WaitingForReset, TimeSpan.FromSeconds(60), "the wait for the reset");

        Assert.True(File.Exists(worktree.File("child-done.txt")));
        var afterwards = WorkspaceFingerprint.Compute(worktree.Path, GitRepo.GitExe).Hash;
        Assert.Equal(afterwards, _root.Task(taskId).BaselineFingerprint);
        Assert.Equal((0, "pre-flight passed"), _root.Ac("preview", taskId, "--now"));
    }
}
