using System.Diagnostics;
using AgentContinuum.Core.Tests;

namespace AgentContinuum.Windows.Tests;

public sealed class ProcessCommandRunnerTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ProcessCommandRunner _runner = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Run_ReturnsExitCodeAndStdout()
    {
        var result = _runner.Run(FakeClaudeLaunch.ExePath, ["--version"], FakeClaudeLaunch.MinimalEnv(), TimeSpan.FromSeconds(10));

        Assert.Equal(new Core.Platform.CommandResult(0, "2.1.304 (Claude Code)\n", false), result with { StdOut = result.StdOut.Replace("\r\n", "\n") });
    }

    [Fact]
    public void Run_UsesExactlyTheGivenEnvironment()
    {
        Environment.SetEnvironmentVariable("AC_ONLY_IN_PARENT", "1");
        var env = FakeClaudeLaunch.MinimalEnv();
        env["AC_ONLY_IN_SPEC"] = "1";
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var result = _runner.Run(cmd, ["/d", "/c", "set"], env, TimeSpan.FromSeconds(10));

        var names = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2)[0]).ToList();
        Assert.Contains("AC_ONLY_IN_SPEC", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("AC_ONLY_IN_PARENT", names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_StdinIsClosedAndEmpty()
    {
        var sink = _dir.File("stdin.bin");

        var result = _runner.Run(FakeClaudeLaunch.ExePath, ["--fake-hook-sink", sink], FakeClaudeLaunch.MinimalEnv(), TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, new FileInfo(sink).Length);
    }

    [Fact]
    public void Run_Timeout_KillsAndReportsTimedOut()
    {
        var watch = Stopwatch.StartNew();

        var result = _runner.Run(FakeClaudeLaunch.ExePath, ["--fake-child", "30"], FakeClaudeLaunch.MinimalEnv(), TimeSpan.FromSeconds(1));

        Assert.True(result.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }
}
