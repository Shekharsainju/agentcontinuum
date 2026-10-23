using System.Runtime.InteropServices;
using AgentContinuum.Core.Platform;

namespace AgentContinuum.Windows.Tests;

public sealed class JobProcessHostTests : IDisposable
{
    private readonly FakeClaudeLaunch _launch = new();

    public void Dispose() => _launch.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    private static async Task<List<string>> ReadAll(IHostedRun run)
    {
        var lines = new List<string>();
        await foreach (var line in run.StdoutLines)
        {
            lines.Add(line);
        }
        return lines;
    }

    [Fact]
    public async Task Start_ChildInJobBeforeRunning()
    {
        _launch.Scenario("""{ "events": [ { "type": "assistant" } ], "exitCode": 7 }""");
        bool? inJob = null;
        bool? ranBeforeResume = null;
        var host = new JobProcessHost(handle =>
        {
            Assert.True(IsProcessInJob(handle, IntPtr.Zero, out var result));
            inJob = result;
            ranBeforeResume = File.Exists(Path.Combine(_launch.WorkingDir, "fake-claude-args.json"));
        });

        using var run = host.Start(_launch.Spec());
        var lines = await ReadAll(run);

        Assert.Equal(7, await run.WaitForExit());
        Assert.True(inJob);
        Assert.False(ranBeforeResume);
        Assert.Contains(lines, line => line.Contains("\"assistant\"", StringComparison.Ordinal));
        Assert.True(run.Pid > 0);
        Assert.True(run.CreatedAtFileTime > DateTime.UtcNow.AddMinutes(-5).ToFileTimeUtc());
    }

    [Fact]
    public async Task Start_ReportsIdentityBeforeRunning()
    {
        _launch.Scenario("""{ "events": [], "exitCode": 0 }""");
        (int Pid, long Created, bool Ran)? reported = null;

        using var run = new JobProcessHost().Start(_launch.Spec(), (pid, created) =>
            reported = (pid, created, File.Exists(Path.Combine(_launch.WorkingDir, "fake-claude-args.json"))));
        await run.WaitForExit();

        Assert.Equal((run.Pid, run.CreatedAtFileTime, false), reported);
    }

    [Fact]
    public void Start_CreatedThrows_ProcessEndedNeverRan()
    {
        _launch.Scenario("""{ "events": [], "exitCode": 0 }""");
        var pid = 0;

        Assert.Throws<InvalidOperationException>(() => new JobProcessHost().Start(_launch.Spec(), (p, _) =>
        {
            pid = p;
            throw new InvalidOperationException("ledger write failed");
        }));

        Assert.True(pid > 0);
        Assert.True(FakeClaudeLaunch.WaitUntil(() => !FakeClaudeLaunch.IsRunning(pid), TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(Path.Combine(_launch.WorkingDir, "fake-claude-args.json")));
    }

    private IHostedRun StartWithChildren()
    {
        _launch.Scenario("""{ "spawnChildren": 2, "lingerSeconds": 30 }""");
        var run = new JobProcessHost().Start(_launch.Spec());
        Assert.True(FakeClaudeLaunch.WaitUntil(
            () => File.Exists(Path.Combine(_launch.WorkingDir, "fake-claude-children.json")), TimeSpan.FromSeconds(10)));
        return run;
    }

    [Fact]
    public async Task Terminate_KillsChildren()
    {
        using var run = StartWithChildren();
        var children = _launch.ChildPids();
        // Each console process also brings a conhost.exe into the job.
        Assert.True(run.ActiveProcesses() >= 3);

        var kill = run.Terminate();

        Assert.Equal(new KillResult(true, 0), kill);
        Assert.True(FakeClaudeLaunch.WaitUntil(() => run.ActiveProcesses() == 0, TimeSpan.FromSeconds(5)));
        Assert.All(children, pid => Assert.False(FakeClaudeLaunch.IsRunning(pid)));
        Assert.Equal(1, await run.WaitForExit().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Terminate_ReturnsWin32Result()
    {
        var run = StartWithChildren();
        run.Dispose();

        Assert.Equal(new KillResult(false, 6), run.Terminate());
    }

    [Fact]
    public void DisposeHost_KillsOnClose()
    {
        var run = StartWithChildren();
        int[] pids = [run.Pid, .. _launch.ChildPids()];

        run.Dispose();

        Assert.True(FakeClaudeLaunch.WaitUntil(() => !pids.Any(FakeClaudeLaunch.IsRunning), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DisposeWhileWaitingForExit_WaitCompletes()
    {
        _launch.Scenario("""{ "lingerSeconds": 30 }""");
        var run = new JobProcessHost().Start(_launch.Spec());
        var exit = run.WaitForExit();

        run.Dispose();

        await exit.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exit.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Env_IsExactlyLaunchSpecEnv()
    {
        _launch.Scenario("""{ "exitCode": 0 }""");
        var env = FakeClaudeLaunch.MinimalEnv();
        env["AC_ONLY_IN_SPEC"] = "1";

        using var run = new JobProcessHost().Start(_launch.Spec(env: env));
        await ReadAll(run);
        Assert.Equal(0, await run.WaitForExit());

        var recorded = _launch.Recorded()["envNames"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Equal(env.Keys.Order(StringComparer.OrdinalIgnoreCase), recorded.Order(StringComparer.OrdinalIgnoreCase));
    }
}
