using System.Diagnostics;
using System.Text.Json;
using AgentContinuum.Cli;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.State;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>AC6: a verified limit signal ends every process in the job within 10 seconds.</summary>
public sealed class AC6LimitKillTests : IDisposable
{
    private readonly AcceptanceRoot _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void AC6_LimitEndsJobWithin10s()
    {
        var resetsAt = _root.Clock.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var worktree = _root.Worktree(AcceptanceRoot.LimitScenario(resetsAt, lingerSeconds: 60, spawnChildren: 2));
        var taskId = _root.AddTask(worktree);
        _root.StartDaemon();

        var reply = _root.Reply(_root.Submit(RequestKinds.Start, taskId), TimeSpan.FromSeconds(60))!;
        Assert.True(reply["launched"]!.GetValue<bool>(), reply.ToJsonString());
        var opId = reply["op"]!.GetValue<string>();
        AcceptanceRoot.Eventually(() => HookRecorded(opId), TimeSpan.FromSeconds(30), "the limit hook");
        var sinceHook = Stopwatch.StartNew();

        int[] pids = [_root.Ops(taskId).Single().Pid!.Value, .. ChildPids(worktree)];
        AcceptanceRoot.Eventually(() => pids.All(AcceptanceRoot.Gone), TimeSpan.FromSeconds(20), "the end of every process");

        Assert.Equal(3, pids.Length);
        Assert.True(sinceHook.Elapsed <= TimeSpan.FromSeconds(10), $"took {sinceHook.Elapsed.TotalSeconds:0.0} s");
        AcceptanceRoot.Eventually(
            () => _root.Task(taskId).State == TaskState.WaitingForReset, TimeSpan.FromSeconds(30), "the wait for the reset");
        Assert.Equal(resetsAt, _root.Task(taskId).ResetExpectedAtEpoch);
    }

    private bool HookRecorded(string opId)
    {
        using var store = StateStore.Open(_root.Database);
        return HookRecorder.ForOp(store, opId).Any(h => h.HookName == AcceptanceRoot.LimitHook);
    }

    private static int[] ChildPids(Core.Tests.GitRepo worktree) =>
        JsonSerializer.Deserialize<int[]>(File.ReadAllText(worktree.File("fake-claude-children.json")))!;
}

