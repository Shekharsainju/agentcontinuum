using System.Diagnostics;
using AgentContinuum.Core.Tests;

namespace AgentContinuum.Cli.Tests;

/// <summary>Runs the built ac.exe as a real process with its own LOCALAPPDATA.</summary>
public sealed class AcProcessTests : IDisposable
{
    private readonly TempDirectory _localAppData = new();

    public void Dispose() => _localAppData.Dispose();

    private (int ExitCode, string StdOut, TimeSpan Elapsed) Ac(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ac.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        info.Environment["LOCALAPPDATA"] = _localAppData.Path;
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ac.exe did not exit within 30 s.");
        }
        return (process.ExitCode, stdout.Result.Trim(), watch.Elapsed);
    }

    [Fact]
    public void State_LivesUnderLocalAppDataFromTheEnvironment()
    {
        var (exit, stdout, _) = Ac("status");

        Assert.Equal((0, "no tasks"), (exit, stdout));
        Assert.True(File.Exists(Path.Combine(_localAppData.Path, "AgentContinuum", "state", "ac.db")));
    }
}
