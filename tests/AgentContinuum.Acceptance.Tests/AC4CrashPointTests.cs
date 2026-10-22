using System.Collections.Concurrent;
using System.Diagnostics;
using AgentContinuum.AcTestHost;
using AgentContinuum.Cli;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Recovery;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>AC4: killing the daemon at each R13 crash point gives the recovery in the architecture doc's table.</summary>
public sealed class AC4CrashPointTests
{
    private const int RunsPerPoint = 20;

    [Fact]
    public void AC4_KillDaemonAtEachCrashPoint()
    {
        var failures = new ConcurrentBag<string>();
        var points = Enum.GetValues<CrashPoint>().Where(p => p != CrashPoint.None).ToArray();
        Parallel.ForEach(points, new ParallelOptions { MaxDegreeOfParallelism = 3 }, point =>
        {
            try
            {
                RunPoint(point);
            }
            catch (Exception e)
            {
                failures.Add($"{point}: {e.Message}");
            }
        });

        Assert.True(failures.IsEmpty, string.Join("\n", failures.Order()));
    }

    // Each daemon start recovers the run the previous daemon died in, then launches the next one.
    private static void RunPoint(CrashPoint point)
    {
        using var root = new AcceptanceRoot();
        string? previous = null;
        for (var run = 0; run <= RunsPerPoint; run++)
        {
            root.Config = root.Config with { CrashPoint = run < RunsPerPoint ? point : CrashPoint.None };
            var daemon = root.StartDaemon();
            if (previous is not null)
            {
                CheckRecovered(root, point, previous, $"run {run - 1}");
            }
            if (run == RunsPerPoint)
            {
                AcceptanceRoot.StopDaemon(daemon);
                return;
            }

            previous = root.AddTask(root.Worktree(AcceptanceRoot.SuccessScenario()));
            root.Submit(RequestKinds.Start, previous);
            if (!daemon.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                AcceptanceRoot.StopDaemon(daemon);
                throw new InvalidOperationException($"run {run}: the daemon did not crash");
            }
            daemon.Dispose();
        }
    }

    private static void CheckRecovered(AcceptanceRoot root, CrashPoint point, string taskId, string run)
    {
        var task = root.Task(taskId);
        var ops = root.Ops(taskId);
        var seen = $"{run}: task {StateText(task.State)} ({task.AttentionReason}), attempts {task.AttemptsUsed}, "
            + $"ops [{string.Join(", ", ops.Select(o => $"{o.State} {o.Outcome}"))}]";
        switch (point)
        {
            case CrashPoint.BeforeIntent:
                Assert.True(task is { State: TaskState.Queued, AttemptsUsed: 0 } && ops.Count == 0, seen);
                break;
            case CrashPoint.BeforeSpawn:
                // The first tick offers it at once, saying the launch did not start.
                Assert.True(
                    task is { State: TaskState.WaitingForReset or TaskState.ResumeOffered, AttemptsUsed: 1 }
                    && ops is [{ State: OpState.Abandoned }],
                    seen);
                AcceptanceRoot.Eventually(
                    () => root.Notifications().Contains($"offer\t{taskId}\tacceptance task: the launch did not start"),
                    TimeSpan.FromSeconds(10),
                    $"{run}: the offer");
                break;
            case CrashPoint.AfterSpawn:
                AssertInterrupted(task, ops, RecoveryService.InterruptedLaunchReason, seen);
                break;
            case CrashPoint.WhileRunning:
                AssertInterrupted(task, ops, RecoveryService.InterruptedRunReason, seen);
                Assert.True(Gone(ops[0].Pid!.Value), $"{seen}: the run's process outlived the daemon");
                break;
            case CrashPoint.AfterExit:
                Assert.True(
                    task is { State: TaskState.Completed, AttemptsUsed: 1 }
                    && ops is [{ State: OpState.Classified, Outcome: Outcome.Completed }],
                    seen);
                break;
        }
    }

    private static void AssertInterrupted(TaskRecord task, IReadOnlyList<OpRow> ops, string reason, string seen) =>
        Assert.True(
            task.State == TaskState.NeedsAttention && task.AttentionReason == reason && task.AttemptsUsed == 1
            && ops is [{ State: OpState.Interrupted }],
            seen);

    private static string StateText(TaskState state) => Core.State.StateText.ToText(state);

    private static bool Gone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
