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
        Assert.Equal(3, run.ActiveProcesses());

        var kill = run.Terminate();

        Assert.Equal(new KillResult(true, 0), kill);
        Assert.True(FakeClaudeLaunch.WaitUntil(() => run.ActiveProcesses() == 0, TimeSpan.FromSeconds(5)));
        Assert.All(children, pid => Assert.False(FakeClaudeLaunch.IsRunning(pid)));
        Assert.Equal(1, await run.WaitForExit().WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
