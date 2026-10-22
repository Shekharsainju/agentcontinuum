using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentContinuum.AcTestHost;
using AgentContinuum.Cli;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Tests;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>AC5: 1,000 randomised crash and restart cycles never exceed max_attempts.</summary>
public sealed class AC5CrashCycleTests
{
    // Each stream runs its own daemon and CLI processes; more than three exhausts an 8 GB machine.
    private const int Streams = 3;
    private const int TotalCycles = 1000;
    private const int MaxAttempts = 3;
    private const int TasksPerStream = 3;

    private static readonly CrashPoint[] Points = Enum.GetValues<CrashPoint>();

    [Fact]
    public void AC5_ThousandCrashCycles_NeverExceedMaxAttempts()
    {
        var failures = new ConcurrentBag<string>();
        var crashes = new ConcurrentDictionary<CrashPoint, int>();
        Parallel.For(0, Streams, new ParallelOptions { MaxDegreeOfParallelism = Streams }, stream =>
        {
            var seed = 5000 + stream;
            try
            {
                RunStream(seed, TotalCycles / Streams + (stream < TotalCycles % Streams ? 1 : 0), crashes);
            }
            catch (Exception e)
            {
                failures.Add($"seed {seed}: {e.Message}");
            }
        });

        Assert.True(failures.IsEmpty, string.Join("\n", failures));
    }

    private static void RunStream(int seed, int cycles, ConcurrentDictionary<CrashPoint, int> crashes)
    {
        var random = new Random(seed);
        using var root = new AcceptanceRoot();
        var tasks = new List<(string Id, GitRepo Worktree)>();
        for (var i = 0; i < TasksPerStream; i++)
        {
            tasks.Add(NewTask(root, random));
        }

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var point = Points[random.Next(Points.Length)];
            root.Config = root.Config with { CrashPoint = point };
            var daemon = root.StartDaemon();
            CheckInvariants(root, tasks, $"cycle {cycle}");

            // A stream whose tasks have all finished or run out keeps cycling on a fresh one.
            if (tasks.All(t => Finished(root.Task(t.Id))))
            {
                tasks.Add(NewTask(root, random));
            }
            var (taskId, worktree) = tasks[random.Next(tasks.Count)];
            AcceptanceRoot.SetScenario(worktree, Scenario(root, random));
            var kind = random.Next(2) == 0 ? RequestKinds.Start : RequestKinds.Resume;
            var request = root.Submit(kind, taskId, now: random.Next(2) == 0);

            if (CrashedBeforeCycleEnd(root, daemon, request, taskId))
            {
                crashes.AddOrUpdate(point, 1, (_, n) => n + 1);
            }
            AcceptanceRoot.StopDaemon(daemon);
            daemon.Dispose();
        }

        root.Config = root.Config with { CrashPoint = CrashPoint.None };
        AcceptanceRoot.StopDaemon(root.StartDaemon());
        CheckInvariants(root, tasks, "final recovery");
    }

    // The daemon either crashes at its point, or replies and the run ends; whichever comes, the cycle ends with
    // the daemon killed, which is itself a crash at whatever point the run had reached.
    private static bool CrashedBeforeCycleEnd(AcceptanceRoot root, Process daemon, long request, string taskId)
    {
        var watch = Stopwatch.StartNew();
        JsonObject? reply = null;
        while (!daemon.HasExited && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            reply ??= root.Reply(request, TimeSpan.Zero);
            if (reply is not null && (reply["launched"]?.GetValue<bool>() != true || root.Task(taskId).State != TaskState.Running))
            {
                return false;
            }
            Thread.Sleep(100);
        }
        return daemon.HasExited;
    }

    private static void CheckInvariants(AcceptanceRoot root, List<(string Id, GitRepo Worktree)> tasks, string when)
    {
        foreach (var (id, worktree) in tasks)
        {
            var task = root.Task(id);
            var ops = root.Ops(id).Count;
            var starts = AcceptanceRoot.StartsLogged(worktree);
            var detail = $"{when}, task {id}: attempts {task.AttemptsUsed}, ops {ops}, starts {starts}, max {task.MaxAttempts}";
            Assert.True(task.AttemptsUsed <= task.MaxAttempts, detail);
            Assert.True(ops == task.AttemptsUsed, detail);
            Assert.True(starts <= task.AttemptsUsed, detail);
            Assert.NotEqual(TaskState.Running, task.State);
        }
    }

    private static bool Finished(TaskRecord task) =>
        task.State is TaskState.Completed or TaskState.Revoked || task.AttemptsUsed >= task.MaxAttempts;

    private static (string, GitRepo) NewTask(AcceptanceRoot root, Random random)
    {
        var worktree = root.Worktree(Scenario(root, random));
        return (root.AddTask(worktree, MaxAttempts), worktree);
    }

    private static JsonObject Scenario(AcceptanceRoot root, Random random) => random.Next(3) switch
    {
        0 => AcceptanceRoot.SuccessScenario(),
        1 => AcceptanceRoot.LimitScenario(root.Clock.UtcNow.AddMinutes(-random.Next(1, 120)).ToUnixTimeSeconds()),
        _ => new JsonObject { ["authSource"] = "none", ["events"] = new JsonArray(), ["exitCode"] = 1 },
    };
}
