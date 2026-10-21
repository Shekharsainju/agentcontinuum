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
}
